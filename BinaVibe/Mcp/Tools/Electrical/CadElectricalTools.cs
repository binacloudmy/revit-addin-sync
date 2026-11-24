#nullable enable
// CAD electrical tools - phase 1 of the CAD electrical to BIM work (spec 2026-11-17 §6).
//
// read_cad_electrical: the drawing's census, cut to fit one tool result, plus what the
// mapping step needs from the model (levels, electrical family types and how they host).
// The AI writes the mapping table from it; plan_cad_electrical then checks and uses it.
//
// Read-only (D9): no Transaction, no scratch link. The drawing is read by path with
// ACadSharp, and its scan is kept for the plan call that follows (D12), keyed by path,
// length and last write time.
//
// The Revit boundary: everything in here may touch the API. The steps it drives
// (census, mapping, fit, resolve, review) are pure and live in their own files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class CadElectricalTools
    {
        public const string Schema = "cad.electrical.census/1";

        private const double MmPerFoot = 304.8;
        private const int MaxScans = 2;
        private static readonly TimeSpan ScanTtl = TimeSpan.FromMinutes(30);

        /// <summary>The categories whose family types a drawing's devices can become.</summary>
        internal static readonly BuiltInCategory[] DeviceCategories =
        {
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_LightingDevices,
            BuiltInCategory.OST_CommunicationDevices,
            BuiltInCategory.OST_DataDevices,
            BuiltInCategory.OST_TelephoneDevices,
            BuiltInCategory.OST_FireAlarmDevices,
            BuiltInCategory.OST_SecurityDevices,
            BuiltInCategory.OST_NurseCallDevices,
        };

        // ─── read_cad_electrical ───────────────────────────────────────
        public static Dictionary<string, object?> ReadCadElectrical(UIApplication app, Document doc, JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref") ?? "";
            string path = ResolvePath(doc, dwgRef);
            ElectricalScan scan = ScanFor(path);
            ElectricalCensusResult census = scan.Census;

            if (census.Drawing.AecRefused)
                return AecRefused(dwgRef, census);

            var notes = new List<object?>();
            if (census.SymbolsAreExploded)
                notes.Add("symbols_are_exploded: every symbol-sized insert sits on a wall layer, so the " +
                          "electrical symbols are probably loose linework. Blocks can only be mapped where " +
                          "they are still blocks.");
            if (!string.IsNullOrEmpty(census.Drawing.AecWarning))
                notes.Add("aec_warning: " + census.Drawing.AecWarning);

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dwg_ref"] = dwgRef,
                ["schema"] = Schema,
                ["census"] = null,
                ["roles"] = CadElectricalMapping.Roles.Cast<object?>().ToList(),
                ["hosts"] = CadElectricalMapping.Hosts.Cast<object?>().ToList(),
                ["layer_roles"] = CadElectricalMapping.LayerRoles.Cast<object?>().ToList(),
                ["model"] = ModelFacts(doc),
                ["notes"] = notes,
            };
            // The census gets what the rest of the result leaves of the budget.
            return CadElectricalReview.WithCensus(result, census);
        }

        // ─── plan_cad_electrical ───────────────────────────────────────
        // Mapping + drawing + model -> one stored plan of one plan cluster on one level (D7),
        // and its review card. Read-only: the plan is cached, place_socket_points commits it.

        private const int MaxPointRows = 2000;

        public static Dictionary<string, object?> PlanCadElectrical(UIApplication app, Document doc, JsonElement args)
        {
            // 1. The drawing.
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref") ?? "";
            string path = ResolvePath(doc, dwgRef);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // link_id: the drawing is placed where the user moved this CAD link or import, and
            // the automatic fit is not run. Checked before the drawing is read: a link of
            // another drawing (the architect's model) fails here, fast.
            long? linkArg = ArgsHelp.GetLong(args, "link_id");
            LinkBasis? givenLink = null;
            FitTransform? givenTransform = null;
            if (linkArg.HasValue)
            {
                if (!(doc.GetElement(ElemIds.From(linkArg.Value)) is ImportInstance givenImp))
                {
                    Dictionary<string, object?> missing = Failed(dwgRef, "link_not_found",
                        "no CAD link or import with id " + linkArg.Value + " in this model — call list_cad_links");
                    missing["link_id"] = linkArg.Value;
                    return missing;
                }
                givenLink = ReadLink(doc, givenImp, out string? unreadable);
                LinkDrawingMismatch? other = CadLinkAlign.OtherDrawing(linkArg.Value, givenLink.FullPath, path);
                if (other != null) return LinkIsOtherDrawing(dwgRef, other);
                givenTransform = unreadable == null ? CadLinkAlign.FromBasis(givenLink, out unreadable) : null;
                if (givenTransform == null) return LinkUnusable(dwgRef, linkArg.Value, unreadable);
            }

            ElectricalScan scan = ScanFor(path);
            if (scan.Census.Drawing.AecRefused) return AecRefused(dwgRef, scan.Census);
            double drawingWidthMm = scan.Census.Drawing.WidthM * 1000, drawingHeightMm = scan.Census.Drawing.HeightM * 1000;
            if (givenLink != null && givenTransform != null &&
                CadLinkAlign.ScaleOff(givenLink, givenTransform, drawingWidthMm, drawingHeightMm))
                return LinkUnusable(dwgRef, linkArg!.Value, "scaled");

            // 2. The mapping, checked against the drawing and the model.
            ModelSnapshot model = CadElectricalResolver.Read(doc);
            var issues = new List<MappingIssue>();
            CadElectricalMapping mapping;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("mapping", out JsonElement raw))
            {
                mapping = CadElectricalMapping.Parse(raw, issues);
            }
            else
            {
                mapping = new CadElectricalMapping();
                issues.Add(new MappingIssue { Path = "", Code = "not_an_object", Message = "mapping is required" });
            }

            var blockKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (CensusBlockClass block in scan.Census.AllBlocks ?? scan.Census.Blocks) blockKeys.Add(block.Key);
            foreach (ScanSymbol symbol in scan.Symbols) blockKeys.Add(symbol.Key);
            var facts = new MappingFacts
            {
                BlockKeys = blockKeys,
                LayerNames = new HashSet<string>(scan.LayerNames, StringComparer.Ordinal),
                PlanCount = scan.Clusters.Count,
                Levels = new HashSet<string>(model.Levels.Select(l => l.Name), StringComparer.Ordinal),
                FamilyTypeMatches = model.FamilyTypeMatches,
                ConduitTypeMatches = text => RunTypeInfo.Matches(model.ConduitTypes, text),
                TrayTypeMatches = text => RunTypeInfo.Matches(model.TrayTypes, text),
                ConduitSizesMm = name => model.ConduitTypes.FirstOrDefault(t => t.Name == name)?.SizesMm ?? new List<double>(),
            };
            mapping.Validate(facts, issues);
            if (issues.Count > 0) return MappingInvalid(dwgRef, issues);

            // The run height for conduit layers whose rows give none (D5: never defaulted).
            double? runHeightArg = ArgsHelp.GetDouble(args, "run_height_mm");
            if (runHeightArg.HasValue && (runHeightArg.Value < CadElectricalMapping.MinRunHeightMm ||
                                          runHeightArg.Value > CadElectricalMapping.MaxRunHeightMm))
                return Failed(dwgRef, "bad_run_height",
                    "run_height_mm must be " + CadElectricalMapping.MinRunHeightMm + " to " +
                    CadElectricalMapping.MaxRunHeightMm + " mm above the level");

            if (scan.Clusters.Count == 0)
                return Failed(dwgRef, "no_background",
                    "no floor plan was found in the drawing: no layer's linework pairs into walls");

            // 3. The plan cluster.
            int cluster;
            long? clusterArg = ArgsHelp.GetLong(args, "cluster");
            if (clusterArg.HasValue)
            {
                if (clusterArg.Value < 0 || clusterArg.Value >= scan.Clusters.Count)
                    return ClusterNeeded(dwgRef, scan, mapping, "cluster_out_of_range",
                        "cluster " + clusterArg.Value + " is not a plan of this drawing");
                cluster = (int)clusterArg.Value;
            }
            else
            {
                List<int> withLevel = mapping.Plans.Where(p => p.Level != null).Select(p => p.Cluster).Distinct().ToList();
                if (withLevel.Count == 1) cluster = withLevel[0];
                else if (scan.Clusters.Count == 1) cluster = 0;
                else
                    return ClusterNeeded(dwgRef, scan, mapping, "cluster_needed",
                        "the drawing has " + scan.Clusters.Count + " plans — pass cluster, or give one plan row a level");
            }

            // Background: the mapping's, else the drawing's wall-like layers, else the
            // layer-name hints the CAD to BIM pane uses.
            List<string> background = mapping.BackgroundLayers();
            bool pairAcross = background.Count > 0;
            if (background.Count == 0) background = scan.WallLikeLayers.ToList();
            if (background.Count == 0)
            {
                var hints = new RevitWebAppSync.Services.CadToBim.CadToBimSettings();
                background = scan.LayerNames.Where(hints.IsWallLayer).ToList();
            }
            if (background.Count == 0)
                return Failed(dwgRef, "no_background",
                    "no wall layer to align the drawing on — name the background layers in the mapping");

            List<FitSegment> cad = scan.WallCentrelines(cluster, background, pairAcross);

            // Fit and host on structural walls only (finish walls under 50 mm left out).
            var structural = new Dictionary<string, List<ModelWall>>(StringComparer.Ordinal);
            var modelByLevel = new Dictionary<string, IReadOnlyList<FitSegment>>(StringComparer.Ordinal);
            foreach (var pair in model.WallsByLevel)
            {
                List<ModelWall> kept = CadSymbolResolve.Structural(pair.Value);
                structural[pair.Key] = kept;
                modelByLevel[pair.Key] = kept.Select(w => new FitSegment(w.X1, w.Y1, w.X2, w.Y2)).ToList();
            }

            // 4. The level: the argument, else the cluster's plan rows when they name one level,
            // else the level the drawing fits best.
            var fitOptions = new FitOptions();
            var fitNotes = new List<string>();
            Level? level = null;
            FitResult? fit = null;
            string? levelName = ArgsHelp.GetString(args, "level");
            if (levelName == null)
            {
                List<string> rowLevels = mapping.Plans
                    .Where(p => p.Cluster == cluster && p.Level != null)
                    .Select(p => p.Level!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (rowLevels.Count > 1)
                {
                    Dictionary<string, object?> needed = Failed(dwgRef, "level_needed",
                        "the mapping gives cluster " + cluster + " more than one level — pass level",
                        rowLevels.Select(l => (object?)l).ToList());
                    needed["cluster"] = cluster;
                    return needed;
                }
                if (rowLevels.Count == 1) levelName = rowLevels[0];
            }
            if (levelName != null)
            {
                level = model.Levels.FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.Ordinal))
                        ?? model.Levels.FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (level == null)
                    return Failed(dwgRef, "level_not_found", "no level '" + levelName + "' in the model",
                        model.Levels.Select(l => (object?)l.Name).ToList());
            }
            else if (givenTransform != null)
            {
                // The link's place measured on every level: the best wins, a near tie asks.
                List<LevelFit> byLink = modelByLevel
                    .Select(pair => new LevelFit { Level = pair.Key, Fit = CadLinkAlign.Measure(cad, pair.Value, givenTransform, fitOptions) })
                    .OrderByDescending(f => f.Fit.Score)
                    .ThenByDescending(f => f.Fit.CoveredMm)
                    .ThenBy(f => f.Level, StringComparer.Ordinal)
                    .ToList();
                if (byLink.Count == 0)
                    return Failed(dwgRef, "no_model_walls", "the model has no levels to place on");
                if (byLink.Count > 1 && byLink[0].Fit.Outcome != "failed" &&
                    byLink[0].Fit.Score - byLink[1].Fit.Score <= CadLinkAlign.TieMargin)
                {
                    Dictionary<string, object?> ambiguous = Failed(dwgRef, "level_ambiguous",
                        "link " + linkArg!.Value + " lays the drawing on several levels about equally (typical floors) — pass level",
                        byLink.Take(5).Select(f => (object?)new Dictionary<string, object?>
                        {
                            ["level"] = f.Level,
                            ["score"] = Math.Round(f.Fit.Score, 3),
                            ["outcome"] = f.Fit.Outcome,
                        }).ToList());
                    ambiguous["cluster"] = cluster;
                    ambiguous["link_id"] = linkArg.Value;
                    return ambiguous;
                }
                level = model.Levels.First(l => l.Name == byLink[0].Level);
            }
            else
            {
                List<LevelFit> fits = CadModelFit.FitLevels(cad, modelByLevel, fitOptions, out bool tie,
                                                            out List<LevelFit> elsewhere);
                if (tie)
                {
                    Dictionary<string, object?> ambiguous = Failed(dwgRef, "level_ambiguous",
                        "the drawing fits several levels about equally (typical floors) — pass level",
                        fits.Take(5).Select(f => (object?)new Dictionary<string, object?>
                        {
                            ["level"] = f.Level,
                            ["score"] = Math.Round(f.Fit.Score, 3),
                            ["outcome"] = f.Fit.Outcome,
                        }).ToList());
                    ambiguous["cluster"] = cluster;
                    return ambiguous;
                }
                if (fits.Count == 0)
                    return Failed(dwgRef, "no_model_walls", "the model has no levels to place on");
                level = model.Levels.First(l => l.Name == fits[0].Level);
                fit = fits[0].Fit;
                foreach (LevelFit other in elsewhere)
                    fitNotes.Add("this cluster also fits " + other.Level + " at another plan; call again with level=" +
                                 other.Level + " to place that one");
            }

            // 5. The placement: a given link's, else the fit (centrelines, then faces), else a
            // positioned link of this drawing found in the model. A passing fit is never overridden.
            List<ModelWall> walls = structural.TryGetValue(level.Name, out List<ModelWall>? onLevel)
                ? onLevel : new List<ModelWall>();
            int thinIgnored = model.WallsByLevel.TryGetValue(level.Name, out List<ModelWall>? allOnLevel)
                ? allOnLevel.Count - walls.Count : 0;
            if (!modelByLevel.ContainsKey(level.Name)) modelByLevel[level.Name] = new List<FitSegment>();
            string method = "fit";
            long? linkUsed = null;
            bool linkAutomatic = false;
            double fitScoreBeforeLink = 0;
            List<object?>? linksTried = null;
            if (givenTransform != null)
            {
                fit = CadLinkAlign.Measure(cad, modelByLevel[level.Name], givenTransform, fitOptions);
                method = CadLinkAlign.Method;
                linkUsed = linkArg!.Value;
                if (fit.Outcome == "failed" && (fit.FailReason == "alignment_failed" || fit.FailReason == "crop_too_small"))
                    return LinkMisaligned(dwgRef, scan, background, pairAcross, cluster, level.Name, linkArg.Value,
                                          modelByLevel[level.Name], fit, fitOptions, clock);
            }
            else
            {
                fit = fit ?? CadModelFit.Fit(cad, modelByLevel[level.Name], fitOptions);
                if (fit.Outcome == "failed" && fit.FailReason != "no_model_walls")
                {
                    ScanCluster box = scan.Clusters[cluster];
                    double margin = new CensusOptions().NearWallMm;
                    List<FitSegment> cadFaces = ElectricalCensus.MergeByDirection(scan.FacePieces(background))
                        .Where(s => s.Length >= fitOptions.MinVoteLengthMm)
                        .Select(s => new FitSegment(s.P1.x, s.P1.y, s.P2.x, s.P2.y))
                        .Where(s =>
                        {
                            double mx = (s.X1 + s.X2) / 2, my = (s.Y1 + s.Y2) / 2;
                            return mx >= box.MinX - margin && mx <= box.MaxX + margin &&
                                   my >= box.MinY - margin && my <= box.MaxY + margin;
                        })
                        .ToList();
                    List<FitSegment> modelFaces = CadModelFit.ModelFaces(
                        walls.Select(w => (new FitSegment(w.X1, w.Y1, w.X2, w.Y2), w.ThicknessMm)));
                    FitResult faces = CadModelFit.Fit(cadFaces, modelFaces, fitOptions);
                    if (faces.Score > fit.Score)
                    {
                        fit = faces;
                        method = "fit_faces";
                    }
                }

                // Both fits failed: a link of this drawing that the user moved onto the building.
                if (fit.Outcome == "failed" && (fit.FailReason == "alignment_failed" || fit.FailReason == "crop_too_small"))
                {
                    IReadOnlyList<FitSegment> levelWalls = modelByLevel[level.Name];
                    List<LinkMeasure> measured = MatchingLinks(doc, path).Select(link =>
                    {
                        FitTransform? t = CadLinkAlign.FromBasis(link, out string? unusable);
                        if (t != null && CadLinkAlign.ScaleOff(link, t, drawingWidthMm, drawingHeightMm))
                        {
                            t = null;
                            unusable = "scaled";
                        }
                        return new LinkMeasure
                        {
                            Id = link.Id,
                            Unusable = unusable,
                            Fit = t != null ? CadLinkAlign.Measure(cad, levelWalls, t, fitOptions) : null,
                        };
                    }).ToList();
                    LinkChoice choice = CadLinkAlign.Choose(measured, automatic: true, fitOptions);
                    if (choice.Status == "ambiguous")
                        return LinkAmbiguous(dwgRef, level.Name, cluster, choice);
                    if (choice.Status == "chosen")
                    {
                        fitScoreBeforeLink = fit.Score;
                        fit = choice.Best!.Fit!;
                        method = CadLinkAlign.Method;
                        linkUsed = choice.Best.Id;
                        linkAutomatic = true;
                    }
                    else if (choice.Ranked.Count > 0)
                        linksTried = choice.Ranked.Select(l => (object?)LinkRow(l)).ToList();
                }
            }
            if (fit.Outcome == "failed")
            {
                string message = fit.FailReason == "crop_too_small"
                    ? "too little of the drawing's wall linework lies on this level: check the background layers and the level"
                    : "the drawing's walls could not be laid on the model's walls on " + level.Name +
                      " (score " + Math.Round(fit.Score, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      "). Check the level and the background layers; or link the DWG into the model, move it " +
                      "onto the building, and run plan_cad_electrical again.";
                if (linksTried != null)
                    message += " The drawing's links in the model (links_tried) do not lay it on the building either.";
                Dictionary<string, object?> failed = Failed(dwgRef, fit.FailReason ?? "alignment_failed", message);
                failed["level"] = level.Name;
                failed["cluster"] = cluster;
                failed["score"] = Math.Round(fit.Score, 3);
                failed["score_uncropped"] = Math.Round(fit.ScoreUncropped, 3);
                failed["inside_share"] = Math.Round(fit.InsideShare, 3);
                failed["method"] = method;
                // Where the fit came closest: the vector to move a link at the origin by.
                if (fit.FailReason == "alignment_failed" || fit.FailReason == "crop_too_small")
                    foreach (var pair in CadLinkAlign.TransformFields(fit.Transform, "best_")) failed[pair.Key] = pair.Value;
                if (linksTried != null) failed["links_tried"] = linksTried;
                return failed;
            }
            if (linkAutomatic)
                fitNotes.Add("the automatic fit failed (score " +
                             Math.Round(fitScoreBeforeLink, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                             "), so the drawing was placed from your positioned link " + linkUsed +
                             "; pass link_id to place from another");

            // Copies of one floor drawn on one sheet fit equally well at each copy: geometry
            // cannot choose, so the caller picks one by the titles under it.
            int pick = -1;
            if (fit.Rivals.Count > 0)
            {
                List<FitResult> places = CadModelFit.Places(fit);
                long? pickArg = ArgsHelp.GetLong(args, "pick");
                if (!pickArg.HasValue || pickArg.Value < 0 || pickArg.Value >= places.Count)
                {
                    Dictionary<string, object?> ambiguous = Failed(dwgRef, "plan_ambiguous",
                        (pickArg.HasValue ? "pick " + pickArg.Value + " is not one of the candidates. " : "") +
                        "the drawing holds " + places.Count + " copies of " + level.Name + "'s walls in cluster " +
                        cluster + " (top to bottom, then left to right) — pass pick with the one whose titles " +
                        "name this level",
                        places.Select((p, i) => (object?)new Dictionary<string, object?>
                        {
                            ["pick"] = i,
                            ["titles"] = TitlesUnder(scan, p).Cast<object?>().ToList(),
                            ["score"] = Math.Round(p.Score, 3),
                        }).ToList());
                    ambiguous["level"] = level.Name;
                    ambiguous["cluster"] = cluster;
                    return ambiguous;
                }
                pick = (int)pickArg.Value;
                fit = places[pick];
                if (fit.Outcome == "failed")
                {
                    Dictionary<string, object?> failed = Failed(dwgRef, "alignment_failed",
                        "pick " + pick + " does not lay the drawing on " + level.Name + "'s walls well enough (score " +
                        Math.Round(fit.Score, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
                    failed["level"] = level.Name;
                    failed["cluster"] = cluster;
                    failed["score"] = Math.Round(fit.Score, 3);
                    foreach (var pair in CadLinkAlign.TransformFields(fit.Transform, "best_")) failed[pair.Key] = pair.Value;
                    return failed;
                }
            }

            // 6-7. Every placeable instance in the cluster, resolved to a point.
            var options = new ResolveOptions();
            var wallIndex = CadSymbolResolve.Index(walls);
            Func<double, double, RoomHit?> roomAt = CadElectricalResolver.RoomLookup(doc, level);
            Func<double, double, CeilingHit?> ceilingAt = CadElectricalResolver.CeilingLookup(doc, level);
            double levelZMm = level.Elevation * MmPerFoot;
            bool provisional = fit.Outcome == "provisional";

            var rows = new Dictionary<string, SymbolRow>(StringComparer.Ordinal);
            foreach (SymbolRow row in mapping.Symbols) rows[row.Block] = row;
            var wallLike = new HashSet<string>(scan.WallLikeLayers, StringComparer.Ordinal);
            double symbolMax = new CensusOptions().SymbolMaxExtentMm;
            Func<string, bool> isMappedPlaceable = key =>
                rows.TryGetValue(key, out SymbolRow? r) && CadElectricalMapping.IsPlaceable(r.Role);

            var points = new List<PlannedPoint>();
            // Each point's drawn symbol on the model: a drawn run ends at the symbol, not the point.
            var drawn = new Dictionary<int, (double X, double Y, double Half)>();
            var unmapped = new Dictionary<string, UnmappedClass>(StringComparer.Ordinal);
            var dropped = new List<object?>();
            // The rest of the drawn symbols, so that `dropped` and `points` together list every
            // symbol drawn on the plan (the exit check's not_in_drawing): ignored and other
            // non-placeable classes, then other plans' placeable symbols. Listed after the real
            // drops, so a cap cuts these first.
            var unplaceable = new List<object?>();
            var otherPlanDrops = new List<object?>();
            int otherPlans = 0, outsideFootprint = 0;
            int total = scan.Symbols.Count;
            var levelBand = CadElectricalResolver.LevelBand(model.Levels, level);
            ModelFootprint? footprint = fit.Footprint != null && !fit.Footprint.IsEmpty ? fit.Footprint : null;

            for (int i = 0; i < total; i++)
            {
                if (i % 200 == 0) McpProgress.Report(i, total);
                ScanSymbol symbol = scan.Symbols[i];
                if (!symbol.Placed) continue;
                rows.TryGetValue(symbol.Key, out SymbolRow? row);

                // A symbol belongs to this plan when, carried onto the model, it lies inside the
                // level's footprint, whatever census cluster it is in (F6). With no footprint
                // the cluster decides, as before.
                fit.Transform.Apply(symbol.X, symbol.Y, out double mx, out double my);
                bool inside = footprint != null ? footprint.Contains(mx, my) : symbol.Cluster == cluster;
                string membership = CadSymbolResolve.Membership(symbol.Cluster, cluster, inside);
                string symbolClass = row?.Role ?? "unmapped";
                // Unmapped blocks that are not symbol-sized or sit on a wall layer (containers,
                // title blocks, the background's own blocks) are not symbols.
                bool notASymbol = row == null && (symbol.IsContainer || Math.Max(symbol.WidthMm, symbol.HeightMm) > symbolMax ||
                                                  wallLike.Contains(symbol.Layer));
                if (membership != "plan")
                {
                    bool placeable = row != null && CadElectricalMapping.IsPlaceable(row.Role);
                    if (!placeable)
                    {
                        if (membership != "other_plan" && !notASymbol)
                            Drop(unplaceable, symbol.Key, membership, mx, my, levelZMm, symbolClass);
                        continue;
                    }
                    if (membership == "other_plan")
                    {
                        otherPlans++;
                        Drop(otherPlanDrops, symbol.Key, membership, mx, my, levelZMm, symbolClass);
                    }
                    else if (!CadSymbolResolve.NestedInMapped(scan.Symbols, i, isMappedPlaceable))
                    {
                        outsideFootprint++;
                        Drop(dropped, symbol.Key, membership, mx, my, levelZMm, symbolClass);
                    }
                    continue;
                }

                // Ignored classes and non-symbols say nothing in the review; an ignored symbol is
                // still listed, as drawn.
                if (row != null && row.Role == "ignore")
                {
                    Drop(unplaceable, symbol.Key, "ignored_class", mx, my, levelZMm, symbolClass);
                    continue;
                }
                if (notASymbol) continue;

                if (CadSymbolResolve.NestedInMapped(scan.Symbols, i, isMappedPlaceable))
                {
                    Drop(dropped, symbol.Key, "nested_in_mapped", mx, my, levelZMm, symbolClass);
                    continue;
                }

                string? reason = row == null ? "not_in_mapping" : CadElectricalMapping.UnplannedReason(row);
                if (reason != null)
                {
                    if (!unmapped.TryGetValue(symbol.Key, out UnmappedClass? u))
                        unmapped[symbol.Key] = u = new UnmappedClass { Block = symbol.Key, Reason = reason };
                    u.Instances++;
                    Drop(dropped, symbol.Key, reason, mx, my, levelZMm, symbolClass);
                    continue;
                }

                string host = row!.Host ?? CadElectricalMapping.DefaultHost(row.Role);
                PlannedPoint p = CadSymbolResolve.Resolve(
                    new ResolveInput
                    {
                        XMm = mx, YMm = my, Role = row.Role, Host = host, MountHeightMm = row.MountHeightMm,
                        // Diagnostic only: where each candidate anchor would put the point.
                        Anchors = CadSymbolResolve.AnchorInputs(symbol, (x, y) =>
                        {
                            fit.Transform.Apply(x, y, out double ax, out double ay);
                            return (ax, ay);
                        }),
                    },
                    walls, wallIndex, roomAt, levelZMm, model.ExistingDevices, options, ceilingAt);
                p.Index = points.Count;
                p.LevelName = level.Name;
                p.SymbolKey = symbol.Key;
                p.FamilyType = row.FamilyType;
                // The placement-time rule too: of the row's category, and in plan on the level band
                // for a device whose contact height is not trusted.
                if (!p.Flags.Contains("already_present") && OverheadPlacement.IsPresent(new PresenceQuery
                    {
                        X = p.XMm, Y = p.YMm, Z = p.ZMm, CeilingId = p.HostCeilingId,
                        CategoryId = row.FamilyType != null && model.CategoryByType.TryGetValue(row.FamilyType, out long category)
                            ? category : (long?)null,
                        BandLowMm = levelBand.Low, BandHighMm = levelBand.High,
                    }, model.Devices, options.ExistingMm))
                    CadSymbolResolve.AddFlag(p, "already_present");
                if (provisional) CadSymbolResolve.AddFlag(p, "provisional_alignment");
                string? hosting = null;
                if (row.FamilyType != null) model.HostingByType.TryGetValue(row.FamilyType, out hosting);
                if (hosting != null && CadSymbolResolve.HostMismatch(host, hosting))
                    CadSymbolResolve.AddFlag(p, "family_host_mismatch");
                if (CadSymbolResolve.NoCeiling(p, host, hosting))
                    CadSymbolResolve.AddFlag(p, "no_ceiling");
                drawn[p.Index] = (mx, my, Math.Max(symbol.WidthMm, symbol.HeightMm) / 2);
                points.Add(p);
            }
            McpProgress.Report(total, total);
            CadSymbolResolve.MarkDuplicates(points, options);

            // 8. Conduit and trunking runs on the run layers (phase 2). A run piece belongs to
            // this plan by the symbols' rule: its midpoint, carried onto the model, inside the
            // footprint (with no footprint, inside the cluster's box).
            FitTransform transform = fit.Transform;
            ModelFootprint? runFootprint = footprint;
            Func<double, double, bool> keepPiece = runFootprint != null
                ? (x, y) =>
                {
                    transform.Apply(x, y, out double px, out double py);
                    return runFootprint.Contains(px, py);
                }
                : scan.InClusterBox(cluster);
            List<LayerRow> runRows = mapping.Layers.Where(r => CadElectricalMapping.IsRunLayer(r.Role)).ToList();
            var runs = new List<PlannedRun>();
            var runOptions = new RunPlanOptions();
            var chainOptions = new ChainOptions();
            var unplannedLayers = new List<UnplannedLayer>();
            int tooShort = 0, freeArcs = 0, piecesOutside = 0, clutter = 0;
            if (runRows.Count > 0)
            {
                // Devices of other levels are no drop targets: only those from this level up to the next.
                double nextLevelZMm = model.Levels.Where(l => l.Elevation > level.Elevation)
                    .Select(l => l.Elevation * MmPerFoot).DefaultIfEmpty(levelZMm + CadElectricalMapping.MaxRunHeightMm).Min();
                List<DropTarget> targets = CadRunPlan.Targets(points, drawn,
                    model.Devices.Where(d => d.Z >= levelZMm - 1000 && d.Z < nextLevelZMm));
                var usedTargets = new HashSet<string>(StringComparer.Ordinal);

                for (int i = 0; i < runRows.Count; i++)
                {
                    McpProgress.Report(i, runRows.Count);
                    LayerRow row = runRows[i];
                    List<Cad2Bim.Segment> pieces = scan.RunPieces(new[] { row.Layer }, keepPiece, out int left);
                    piecesOutside += left;
                    string? reason = CadElectricalMapping.UnplannedReason(row, runHeightArg);
                    ChainResult? chain = null;
                    if (reason == null)
                    {
                        List<ArcSpan> arcs = scan.RunArcs(new[] { row.Layer }, keepPiece, out _);
                        chain = CadRunChain.Chain(pieces, arcs, ChainOptions.ForKind(row.Role == "trunking" ? "cable_tray" : "conduit"));
                        reason = chain.Error;
                    }
                    if (reason != null || chain == null)
                    {
                        unplannedLayers.Add(new UnplannedLayer
                        {
                            Layer = row.Layer, Reason = reason ?? "", Pieces = pieces.Count, LengthMm = pieces.Sum(s => s.Length),
                        });
                        continue;
                    }

                    tooShort += chain.TooShort;
                    freeArcs += chain.FreeArcs;
                    clutter += chain.ClutterDropped;
                    var spec = new RunLayerSpec
                    {
                        Layer = row.Layer,
                        Kind = row.Role == "trunking" ? "cable_tray" : "conduit",
                        TypeName = row.Role == "trunking" ? row.TrayType : row.ConduitType,
                        DiameterMm = row.DiameterMm,
                        WidthMm = row.WidthMm,
                        HeightMm = row.HeightMm,
                        RunHeightMm = CadElectricalMapping.EffectiveRunHeight(row, runHeightArg),
                    };
                    runs.AddRange(CadRunPlan.Plan(chain.Runs, spec, transform, levelZMm, level.Name, targets, provisional,
                                                  runOptions, runs.Count, usedTargets));
                }
                McpProgress.Report(runRows.Count, runRows.Count);
            }

            // 9. Store, then review.
            var plan = new SocketPlan
            {
                Points = points,
                Runs = runs,
                Source = "cad",
                SourceFile = Path.GetFileName(path),
                Alignment = new PlanAlignment
                {
                    Level = level.Name,
                    Cluster = cluster,
                    RotationDeg = fit.Transform.RotationRad * 180 / Math.PI,
                    TxMm = fit.Transform.TxMm,
                    TyMm = fit.Transform.TyMm,
                    Score = fit.Score,
                    ScoreUncropped = fit.ScoreUncropped,
                    InsideShare = fit.InsideShare,
                    ModelCoverage = fit.ModelCoverage,
                    Pick = pick,
                    Outcome = fit.Outcome,
                    Method = method,
                    LinkId = linkUsed,
                },
                ParamsUsed = new Dictionary<string, object?>
                {
                    ["dwg_ref"] = dwgRef,
                    ["cluster"] = cluster,
                    ["level"] = level.Name,
                    ["pick"] = pick >= 0 ? pick : (int?)null,
                    ["link_id"] = linkUsed,
                    ["background_layers"] = background.Cast<object?>().ToList(),
                    ["pair_across_layers"] = pairAcross,
                    ["min_wall_mm"] = CadSymbolResolve.MinStructuralWallMm,
                    ["thin_walls_ignored"] = thinIgnored,
                    ["height_basis"] = "level",
                    ["ceiling_height_basis"] = "ceiling_underside, else level",
                    ["reach_mm"] = options.ReachMm,
                    ["axial_tolerance_mm"] = options.AxialTolMm,
                    ["existing_mm"] = options.ExistingMm,
                    ["duplicate_mm"] = options.DuplicateMm,
                    ["wet_room_keywords"] = options.WetKeywords.Cast<object?>().ToList(),
                    ["run_height_mm"] = runHeightArg,
                    ["run_join_mm"] = chainOptions.JoinMm,
                    ["min_run_mm"] = ChainOptions.MinRunMmFor("conduit"),
                    ["min_tray_run_mm"] = ChainOptions.MinRunMmFor("cable_tray"),
                    ["clutter_max_mm"] = chainOptions.ClutterMaxMm,
                    ["clutter_axis_tolerance_deg"] = chainOptions.AxisToleranceDeg,
                    ["drop_reach_mm"] = runOptions.DropReachMm,
                    ["min_drop_mm"] = runOptions.MinDropMm,
                    ["run_height_basis"] = "level",
                },
            };
            string planId = SocketPlanCache.Store(plan, SocketCandidates.DocKey(doc));

            var notes = new List<string>(mapping.Notes);
            notes.AddRange(fitNotes);
            if (runHeightArg.HasValue && runRows.Count == 0)
                notes.Add("run_height_mm was given but the mapping has no conduit or trunking layer; it was not used");
            if (provisional && method == CadLinkAlign.Method)
                notes.Add("the alignment from your positioned link is provisional (score under 0.50): the link lays only " +
                          "part of the drawing's walls on this level's walls; check a few points before placing.");
            else if (provisional)
                notes.Add("the alignment is provisional (score under 0.80): the drawing very likely sits correctly; " +
                          "check a few points before placing. This is not a misalignment.");

            Dictionary<string, object?>? conduitBlock = null;
            if (runRows.Count > 0)
            {
                string status = unplannedLayers.Any(u => u.Reason == "no_run_height") ? "needs_run_height" : "planned";
                conduitBlock = CadElectricalReview.ConduitReview(plan, unplannedLayers, tooShort, freeArcs, status, piecesOutside, clutter);
            }

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["dwg_ref"] = dwgRef,
                ["level"] = level.Name,
                ["cluster"] = cluster,
                ["review"] = CadElectricalReview.PlanReview(plan, unmapped.Values.ToList(), otherPlans, notes, outsideFootprint,
                                                            conduitBlock),
                ["params_used"] = plan.ParamsUsed,
            };

            if (ArgsHelp.GetBool(args, "include_points") ?? false)
            {
                result["points"] = points.Take(MaxPointRows).Select(p => (object?)new Dictionary<string, object?>
                {
                    ["index"] = p.Index,
                    ["role"] = p.Role,
                    ["block"] = p.SymbolKey,
                    ["room"] = p.RoomName,
                    ["host"] = p.Host,
                    ["xyz_mm"] = new List<object?> { Math.Round(p.XMm, 1), Math.Round(p.YMm, 1), Math.Round(p.ZMm, 1) },
                    ["height_above_level_mm"] = CadElectricalReview.HeightAboveLevelMm(p.ZMm, levelZMm),
                    ["facing"] = new List<object?> { Math.Round(p.FacingDx, 4), Math.Round(p.FacingDy, 4) },
                    ["flags"] = p.Flags.Cast<object?>().ToList(),
                    ["excluded"] = p.Excluded,
                    // Diagnostic only (the anchor diagnostic, docs): where each candidate anchor
                    // of the drawn symbol would put this point; null when it does not apply.
                    ["anchors_mm"] = p.Anchors?.ToDictionary(a => a.Name, a => a.Known
                        ? (object?)new List<object?> { Math.Round(a.XMm, 1), Math.Round(a.YMm, 1), Math.Round(a.ZMm, 1) }
                        : null),
                    ["along_offset_mm"] = p.Anchors?.ToDictionary(a => a.Name, a => a.Known && a.AlongOffsetMm.HasValue
                        ? (object?)Math.Round(a.AlongOffsetMm.Value, 1)
                        : null),
                }).ToList();
                result["points_truncated"] = points.Count > MaxPointRows;
                int ownSymbols = dropped.Count + unplaceable.Count;
                result["dropped"] = dropped.Concat(unplaceable).Concat(otherPlanDrops).Take(MaxPointRows).ToList();
                result["dropped_truncated"] = ownSymbols + otherPlanDrops.Count > MaxPointRows;
                // True when points and dropped together list every symbol drawn on this plan:
                // only other plans' symbols were cut, if any.
                result["dropped_lists_every_symbol"] = ownSymbols <= MaxPointRows && points.Count <= MaxPointRows;
                result["runs"] = runs.Take(MaxPointRows).Select(r => (object?)new Dictionary<string, object?>
                {
                    ["index"] = r.Index,
                    ["layer"] = r.Layer,
                    ["kind"] = r.Kind,
                    ["points_mm"] = r.PointsMm.Select(p => (object?)new List<object?>
                    {
                        Math.Round(p[0], 1), Math.Round(p[1], 1), Math.Round(p[2], 1),
                    }).ToList(),
                    ["start"] = RunEndRow(r.Start),
                    ["end"] = RunEndRow(r.End),
                    ["length_mm"] = Math.Round(r.LengthMm, 1),
                    ["flags"] = r.Flags.Cast<object?>().ToList(),
                    ["excluded"] = r.Excluded,
                }).ToList();
                result["runs_truncated"] = runs.Count > MaxPointRows;
            }
            return result;
        }

        // ─── the positioned CAD link (plan step 5) ──────────────────────

        /// <summary>A CAD link or import and its total transform; <paramref name="unreadable"/>
        /// says why there is none.</summary>
        private static LinkBasis ReadLink(Document doc, ImportInstance imp, out string? unreadable)
        {
            unreadable = null;
            string path = DwgReader.PathOf(doc, imp);
            var link = new LinkBasis
            {
                Id = imp.Id.Value,
                IsLinked = imp.IsLinked,
                File = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path),
                FullPath = string.IsNullOrEmpty(path) ? null : path,
            };
            try
            {
                Transform t = imp.GetTotalTransform();
                link.OriginMm = new[] { t.Origin.X * MmPerFoot, t.Origin.Y * MmPerFoot, t.Origin.Z * MmPerFoot };
                link.BasisX = Xyz(t.BasisX);
                link.BasisY = Xyz(t.BasisY);
                link.BasisZ = Xyz(t.BasisZ);
            }
            catch (Exception) { unreadable = "without a readable transform"; }
            BoundingBoxXYZ? box = imp.get_BoundingBox(null);
            if (box != null)
            {
                link.WidthMm = (box.Max.X - box.Min.X) * MmPerFoot;
                link.HeightMm = (box.Max.Y - box.Min.Y) * MmPerFoot;
            }
            return link;
        }

        /// <summary>link_unusable for link_id: mirrored, tilted, scaled (its extent is not the
        /// drawing's: a link in another unit) or without a readable transform.</summary>
        private static Dictionary<string, object?> LinkUnusable(string dwgRef, long linkId, string? reason)
        {
            Dictionary<string, object?> unusable = Failed(dwgRef, "link_unusable",
                "link " + linkId + " is " + reason + ": only a link that is turned and moved, " +
                "not mirrored, tilted or scaled, can place the drawing" +
                (reason == "scaled" ? " (its size is not the drawing's: relink the DWG with link_file, which links it in the drawing's unit)" : ""));
            unusable["link_id"] = linkId;
            unusable["reason"] = reason;
            return unusable;
        }

        /// <summary>
        /// The CAD links of the model (imports have no file and are only used by link_id) whose
        /// file is the drawing's: by full path, else by file name. An att: ref resolves to the
        /// file the pane attached, not a copy, so its path matches a link of the same file.
        /// </summary>
        private static List<LinkBasis> MatchingLinks(Document doc, string path)
        {
            string full = SafeFullPath(path);
            string name = Path.GetFileName(path);
            var byPath = new List<LinkBasis>();
            var byName = new List<LinkBasis>();
            foreach (ImportInstance imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                         .Cast<ImportInstance>())
            {
                if (!imp.IsLinked) continue;
                string linked = DwgReader.PathOf(doc, imp);
                if (string.IsNullOrEmpty(linked)) continue;
                bool samePath = string.Equals(SafeFullPath(linked), full, StringComparison.OrdinalIgnoreCase);
                bool sameName = string.Equals(Path.GetFileName(linked), name, StringComparison.OrdinalIgnoreCase);
                if (!samePath && !sameName) continue;
                LinkBasis link = ReadLink(doc, imp, out string? unreadable);
                if (unreadable != null) continue;
                (samePath ? byPath : byName).Add(link);
            }
            return byPath.Count > 0 ? byPath : byName;
        }

        private static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }

        /// <summary>links_tried and link_ambiguous rows: the link, its score and outcome under
        /// its own transform, and where it lays the drawing.</summary>
        private static Dictionary<string, object?> LinkRow(LinkMeasure link)
        {
            var row = new Dictionary<string, object?>
            {
                ["link_id"] = link.Id,
                ["score"] = link.Fit != null ? Math.Round(link.Fit.Score, 3) : (double?)null,
                ["outcome"] = link.Fit?.Outcome,
                ["unusable"] = link.Unusable,
            };
            if (link.Fit != null)
                foreach (var pair in CadLinkAlign.TransformFields(link.Fit.Transform)) row[pair.Key] = pair.Value;
            return row;
        }

        private static Dictionary<string, object?> LinkAmbiguous(string dwgRef, string level, int cluster, LinkChoice choice)
        {
            Dictionary<string, object?> ambiguous = Failed(dwgRef, "link_ambiguous",
                "the model holds " + choice.Ranked.Count + " links of this drawing and the fit failed — pass link_id " +
                "with the link moved onto " + level,
                choice.Ranked.Select(l => (object?)LinkRow(l)).ToList());
            ambiguous["level"] = level;
            ambiguous["cluster"] = cluster;
            return ambiguous;
        }

        /// <summary>link_id given, and its place does not lay the cluster on the level: every
        /// cluster's score under the link, so a link laid on another plan of the sheet shows.</summary>
        private static Dictionary<string, object?> LinkMisaligned(string dwgRef, ElectricalScan scan, List<string> background,
                                                                  bool pairAcross, int cluster, string level, long linkId,
                                                                  IReadOnlyList<FitSegment> levelWalls, FitResult fit,
                                                                  FitOptions fitOptions, System.Diagnostics.Stopwatch clock)
        {
            // Bounded (the add-in's HTTP call is capped at 50 s): the walls are paired once for
            // every cluster, clusters without walls are skipped, at most MaxMeasuredClusters
            // others are measured, and none once the plan has run LinkScoreBudget.
            bool truncated = scan.Clusters.Count > 1;
            var scores = new List<(int Cluster, double Score)>();
            if (clock.Elapsed < LinkScoreBudget)
            {
                List<List<FitSegment>> byCluster = scan.WallCentrelinesByCluster(background, pairAcross);
                scores = CadLinkAlign.ClusterScores(scan.Clusters.Count,
                    c => c == cluster ? (IReadOnlyList<FitSegment>)new List<FitSegment>() : byCluster[c],
                    levelWalls, fit.Transform, fitOptions, CadLinkAlign.MaxMeasuredClusters,
                    () => clock.Elapsed >= LinkScoreBudget, out truncated);
            }
            scores.Add((cluster, fit.Score));
            scores = scores.OrderByDescending(s => s.Score).ThenBy(s => s.Cluster).ToList();
            var titles = scan.Census.Plans.GroupBy(p => p.Cluster).ToDictionary(g => g.Key, g => g.First().Titles);
            (int Cluster, double Score) better = scores.FirstOrDefault(s => s.Cluster != cluster && s.Score >= CadLinkAlign.FloorScore &&
                                                                            s.Score > fit.Score);
            string message = "link " + linkId + " does not lay cluster " + cluster + " on " + level + "'s walls (score " +
                             Math.Round(fit.Score, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                             ", under " + CadLinkAlign.FloorScore.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                             "). Move the link onto the building, or check the level and the cluster.";
            if (fit.FailReason == "crop_too_small")
                message = "link " + linkId + " lays too little of cluster " + cluster + "'s walls inside " + level +
                          "'s walls. Move the link onto the building, or check the level and the cluster.";
            if (better.Cluster != cluster && better.Score > 0)
                message += " Under this link cluster " + better.Cluster + " scores " +
                           Math.Round(better.Score, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                           ": call again with cluster=" + better.Cluster + " if that is the plan you placed.";
            Dictionary<string, object?> failed = Failed(dwgRef, "link_misaligned", message,
                scores.Select(s => (object?)new Dictionary<string, object?>
                {
                    ["cluster"] = s.Cluster,
                    ["titles"] = (titles.TryGetValue(s.Cluster, out List<string>? t) ? t : new List<string>()).Cast<object?>().ToList(),
                    ["score"] = Math.Round(s.Score, 3),
                }).ToList());
            failed["link_id"] = linkId;
            failed["level"] = level;
            failed["cluster"] = cluster;
            failed["score"] = Math.Round(fit.Score, 3);
            failed["score_uncropped"] = Math.Round(fit.ScoreUncropped, 3);
            failed["inside_share"] = Math.Round(fit.InsideShare, 3);
            failed["reason"] = fit.FailReason;
            failed["clusters_truncated"] = truncated;
            foreach (var pair in CadLinkAlign.TransformFields(fit.Transform)) failed[pair.Key] = pair.Value;
            return failed;
        }

        /// <summary>Plan time after which link_misaligned measures no more clusters.</summary>
        private static readonly TimeSpan LinkScoreBudget = TimeSpan.FromSeconds(30);

        private static Dictionary<string, object?> LinkIsOtherDrawing(string dwgRef, LinkDrawingMismatch other)
        {
            Dictionary<string, object?> failed = Failed(dwgRef, CadLinkAlign.OtherDrawingError, other.Message);
            failed["link_id"] = other.LinkId;
            failed["link_file"] = other.LinkFile;
            failed["drawing_file"] = other.DrawingFile;
            return failed;
        }

        private static Dictionary<string, object?> RunEndRow(RunEnd end) => new Dictionary<string, object?>
        {
            ["kind"] = end.Kind,
            ["point_index"] = end.PointIndex,
            ["element_id"] = end.ElementId,
            ["drop_mm"] = Math.Round(end.DropMm, 1),
        };

        // ─── dump_electrical_reference ─────────────────────────────────
        // The exit check's measuring tape (add-in only, not advertised): every device of
        // the open model, its links' placements and, on request, the level's wall
        // centrelines, written to a private file for PlacementCompare and the headless
        // --fit. A real model's dump carries its project's names, so it is refused inside
        // any git working tree. Read-only.

        public static Dictionary<string, object?> DumpElectricalReference(Document doc, JsonElement args)
        {
            var outPath = ArgsHelp.GetString(args, "out_path")
                ?? throw new ArgumentException("missing out_path");
            string full = Path.GetFullPath(outPath);
            if (InsideGitWorkTree(full))
                throw new InvalidOperationException("refusing to write the reference inside a git working tree: " + full);
            string? dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                throw new InvalidOperationException("no such folder: " + dir);

            string? levelName = ArgsHelp.GetString(args, "level");
            bool includeWalls = ArgsHelp.GetBool(args, "include_walls") ?? false;
            Level? level = null;
            if (levelName != null)
            {
                level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("no level '" + levelName + "' in the model");
            }

            var devices = new List<object?>();
            // One placed instance per device FamilySymbol, in first-seen order, for device_types.
            var byType = new Dictionary<long, (FamilyInstance First, int Count)>();
            var typeOrder = new List<long>();
            var filter = new ElementMulticategoryFilter(DeviceCategories.ToList());
            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter))
            {
                if (!(el.Location is LocationPoint lp)) continue;
                var fi = el as FamilyInstance;
                Level? on = DeviceLevel(doc, el);
                if (level != null && (on == null || on.Id != level.Id)) continue;

                if (fi?.Symbol != null)
                {
                    long typeId = fi.Symbol.Id.Value;
                    if (byType.TryGetValue(typeId, out var seen)) byType[typeId] = (seen.First, seen.Count + 1);
                    else { byType[typeId] = (fi, 1); typeOrder.Add(typeId); }
                }

                // A face-hosted device faces out of its host face; its FacingOrientation lies in
                // that face (up, on a wall) and would read [0,0] in plan.
                XYZ? facing = fi != null ? SocketPlacement.FaceHostedFacing(fi) : null;
                if (facing == null)
                {
                    try { facing = fi?.FacingOrientation; } catch { facing = null; }
                }
                devices.Add(new Dictionary<string, object?>
                {
                    ["id"] = el.Id.Value,
                    ["category"] = el.Category?.Name ?? "",
                    ["family_type"] = fi?.Symbol != null ? FullName(fi.Symbol) : el.Name,
                    ["level"] = on?.Name,
                    ["xyz_mm"] = Mm(lp.Point),
                    ["offset_mm"] = on != null ? Math.Round((lp.Point.Z - on.Elevation) * MmPerFoot, 1) : (double?)null,
                    ["host_kind"] = HostKindOf(fi),
                    ["facing"] = facing == null
                        ? null
                        : new List<object?> { Math.Round(facing.X, 4), Math.Round(facing.Y, 4) },
                });
            }

            var links = new List<object?>();
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                Transform t;
                try { t = link.GetTotalTransform(); } catch { continue; }
                links.Add(new Dictionary<string, object?>
                {
                    ["id"] = link.Id.Value,
                    ["name"] = link.Name,
                    ["loaded"] = link.GetLinkDocument() != null,
                    ["origin_mm"] = Mm(t.Origin),
                    ["basis_x"] = Unit(t.BasisX),
                    ["basis_y"] = Unit(t.BasisY),
                    ["basis_z"] = Unit(t.BasisZ),
                });
            }

            var deviceTypes = typeOrder.Select(id => (object?)DeviceTypeRow(byType[id].First, byType[id].Count)).ToList();
            List<object?> cadImports = CadImportRows(doc);

            var dump = new Dictionary<string, object?>
            {
                ["schema"] = "cad.electrical.reference/1",
                ["level"] = level?.Name,
                ["devices"] = devices,
                ["links"] = links,
                ["cad_imports"] = cadImports,
                ["device_types"] = deviceTypes,
            };

            int wallCount = 0;
            if (includeWalls)
            {
                ModelSnapshot model = CadElectricalResolver.Read(doc);
                Func<List<ModelWall>, List<object?>> rows = walls => walls.Select(w => (object?)new Dictionary<string, object?>
                {
                    ["id"] = w.Id,
                    ["x1"] = Math.Round(w.X1, 1),
                    ["y1"] = Math.Round(w.Y1, 1),
                    ["x2"] = Math.Round(w.X2, 1),
                    ["y2"] = Math.Round(w.Y2, 1),
                    ["thickness_mm"] = Math.Round(w.ThicknessMm, 1),
                    ["linked"] = w.Linked,
                }).ToList();

                if (level != null)
                {
                    List<ModelWall> walls = model.WallsByLevel.TryGetValue(level.Name, out List<ModelWall>? w) ? w : new List<ModelWall>();
                    dump["walls"] = rows(walls);
                    wallCount = walls.Count;
                }
                else
                {
                    var byLevel = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var pair in model.WallsByLevel)
                    {
                        byLevel[pair.Key] = rows(pair.Value);
                        wallCount += pair.Value.Count;
                    }
                    dump["walls_by_level"] = byLevel;
                }
            }

            int runCount = 0;
            if (ArgsHelp.GetBool(args, "include_runs") ?? false)
            {
                List<object?> runs = RunRows(doc, level);
                dump["runs"] = runs;
                runCount = runs.Count;
            }

            File.WriteAllText(full, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["out_path"] = full,
                ["devices"] = devices.Count,
                ["links"] = links.Count,
                ["walls"] = wallCount,
                ["runs"] = runCount,
                ["cad_imports"] = cadImports.Count,
                ["device_types"] = deviceTypes.Count,
            };
        }

        /// <summary>Every CAD import or link of the document and its total transform: does the
        /// transform carry the drawing's unit scale? Origin in mm; basis lengths unitless.</summary>
        private static List<object?> CadImportRows(Document doc)
        {
            var rows = new List<object?>();
            foreach (ImportInstance imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                         .Cast<ImportInstance>())
            {
                string? file = null;
                try
                {
                    string path = DwgReader.PathOf(doc, imp);
                    file = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
                }
                catch { file = null; }

                var row = new Dictionary<string, object?>
                {
                    ["id"] = imp.Id.Value,
                    ["file"] = file,
                    ["is_linked"] = imp.IsLinked,
                };
                try
                {
                    Transform t = imp.GetTotalTransform();
                    foreach (var pair in ReferenceDumpFacts.TransformFacts(Xyz(t.Origin), Xyz(t.BasisX), Xyz(t.BasisY), Xyz(t.BasisZ)))
                        row[pair.Key] = pair.Value;
                }
                catch (Exception ex) { row["transform_error"] = ex.Message; }
                rows.Add(row);
            }
            return rows;
        }

        private static double[] Xyz(XYZ v) => new[] { v.X, v.Y, v.Z };

        /// <summary>One device type of the dump: its count and the connectors of one placed
        /// instance (MEPModel's ConnectorManager), by domain. Do socket families have conduit
        /// connectors?</summary>
        private static Dictionary<string, object?> DeviceTypeRow(FamilyInstance sample, int count)
        {
            var connectors = new List<object?>();
            var domains = new List<string>();
            bool hasManager = false;
            try
            {
                ConnectorManager? cm = MutatorsMepRouting.GetConnectorManager(sample);
                if (cm != null)
                {
                    hasManager = true;
                    foreach (Connector c in cm.Connectors)
                    {
                        string domain = c.Domain.ToString();
                        domains.Add(domain);
                        var row = new Dictionary<string, object?>
                        {
                            ["id"] = c.Id,
                            ["domain"] = domain,
                            ["connector_type"] = c.ConnectorType.ToString(),
                        };
                        try
                        {
                            XYZ z = c.CoordinateSystem.BasisZ;
                            row["direction"] = new List<object?> { Math.Round(z.X, 4), Math.Round(z.Y, 4), Math.Round(z.Z, 4) };
                        }
                        catch { row["direction"] = null; }
                        connectors.Add(row);
                    }
                }
            }
            catch (Exception ex)
            {
                connectors.Add(new Dictionary<string, object?> { ["error"] = ex.Message });
            }

            return new Dictionary<string, object?>
            {
                ["family_type"] = FullName(sample.Symbol),
                ["category"] = sample.Category?.Name ?? "",
                ["count"] = count,
                ["sample_id"] = sample.Id.Value,
                ["has_connector_manager"] = hasManager,
                ["connectors"] = connectors,
                ["has_conduit_connector"] = ReferenceDumpFacts.HasConduitConnector(domains),
            };
        }

        /// <summary>Every conduit and cable tray segment of the model (of one level when given):
        /// id, kind, type, size and both ends in mm. Fittings are left out.</summary>
        private static List<object?> RunRows(Document doc, Level? level)
        {
            var rows = new List<object?>();
            var filter = new ElementMulticategoryFilter(new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray,
            });
            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter))
            {
                if (!(el is MEPCurve curve) || !(curve.Location is LocationCurve lc) || lc.Curve == null) continue;
                Level? on = null;
                try { on = curve.ReferenceLevel; } catch { on = null; }
                if (level != null && (on == null || on.Id != level.Id)) continue;

                bool tray = curve is Autodesk.Revit.DB.Electrical.CableTray;
                var type = doc.GetElement(curve.GetTypeId()) as ElementType;
                var row = new Dictionary<string, object?>
                {
                    ["id"] = el.Id.Value,
                    ["kind"] = tray ? "cable_tray" : "conduit",
                    ["type"] = type != null ? type.FamilyName + " : " + type.Name : null,
                    ["level"] = on?.Name,
                    ["start_mm"] = Mm(lc.Curve.GetEndPoint(0)),
                    ["end_mm"] = Mm(lc.Curve.GetEndPoint(1)),
                };
                if (tray)
                {
                    row["width_mm"] = ParamMm(curve, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                    row["height_mm"] = ParamMm(curve, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                }
                else
                {
                    row["diameter_mm"] = ParamMm(curve, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static double? ParamMm(Element el, BuiltInParameter bip)
        {
            try
            {
                Parameter? p = el.get_Parameter(bip);
                return p != null && p.StorageType == StorageType.Double ? Math.Round(p.AsDouble() * MmPerFoot, 1) : (double?)null;
            }
            catch { return null; }
        }

        private static Level? DeviceLevel(Document doc, Element el)
        {
            if (doc.GetElement(el.LevelId) is Level own) return own;
            try
            {
                Parameter? p = el.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                if (p != null && p.StorageType == StorageType.ElementId && doc.GetElement(p.AsElementId()) is Level scheduled)
                    return scheduled;
            }
            catch { /* no schedule level */ }
            return null;
        }

        private static string HostKindOf(FamilyInstance? fi)
        {
            if (fi == null) return "none";
            Element? host = fi.Host;
            if (host is Wall) return "wall";
            if (host is Ceiling) return "ceiling";
            if (fi.HostFace != null) return "face";
            if (host == null) return "none";
            return (host.Category?.Name ?? "other").ToLowerInvariant();
        }

        private static List<object?> Mm(XYZ p) =>
            new List<object?> { Math.Round(p.X * MmPerFoot, 1), Math.Round(p.Y * MmPerFoot, 1), Math.Round(p.Z * MmPerFoot, 1) };

        private static List<object?> Unit(XYZ v) =>
            new List<object?> { Math.Round(v.X, 9), Math.Round(v.Y, 9), Math.Round(v.Z, 9) };

        private static bool InsideGitWorkTree(string file)
        {
            DirectoryInfo? dir = new FileInfo(file).Directory;
            while (dir != null)
            {
                string git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return true;
                dir = dir.Parent;
            }
            return false;
        }

        /// <summary>The plan titles under the part of the drawing a placement lays on the level.</summary>
        private static List<string> TitlesUnder(ElectricalScan scan, FitResult place) =>
            CadModelFit.DrawingBox(place, out double minX, out double minY, out double maxX, out double maxY)
                ? scan.TitlesIn(minX, minY, maxX, maxY)
                : new List<string>();

        /// <summary>One `dropped` row. <paramref name="symbolClass"/> is the mapping row's role,
        /// or "unmapped".</summary>
        private static void Drop(List<object?> dropped, string block, string reason, double x, double y, double z,
                                 string symbolClass) =>
            dropped.Add(new Dictionary<string, object?>
            {
                ["block"] = block,
                ["class"] = symbolClass,
                ["reason"] = reason,
                ["xyz_mm"] = new List<object?> { Math.Round(x, 1), Math.Round(y, 1), Math.Round(z, 1) },
            });

        private static Dictionary<string, object?> MappingInvalid(string dwgRef, List<MappingIssue> issues) =>
            new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["dwg_ref"] = dwgRef,
                ["error"] = "mapping_invalid",
                ["issues"] = issues.Select(i => (object?)new Dictionary<string, object?>
                {
                    ["path"] = i.Path,
                    ["code"] = i.Code,
                    ["message"] = i.Message,
                    ["candidates"] = i.Candidates.Cast<object?>().ToList(),
                }).ToList(),
            };

        private static Dictionary<string, object?> ClusterNeeded(string dwgRef, ElectricalScan scan,
                                                                 CadElectricalMapping mapping, string error, string message)
        {
            List<object?> candidates = scan.Census.Plans.Select(p => (object?)new Dictionary<string, object?>
            {
                ["cluster"] = p.Cluster,
                ["titles"] = p.Titles.Cast<object?>().ToList(),
                ["symbols"] = p.Symbols,
                ["width_m"] = Math.Round(p.WidthM, 1),
                ["height_m"] = Math.Round(p.HeightM, 1),
                ["level"] = mapping.Plans.FirstOrDefault(r => r.Cluster == p.Cluster)?.Level,
            }).ToList();
            return Failed(dwgRef, error, message, candidates);
        }

        private static Dictionary<string, object?> Failed(string dwgRef, string error, string message,
                                                          List<object?>? candidates = null)
        {
            var result = new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["dwg_ref"] = dwgRef,
                ["error"] = error,
                ["message"] = message,
            };
            if (candidates != null) result["candidates"] = candidates;
            return result;
        }

        internal static Dictionary<string, object?> AecRefused(string dwgRef, ElectricalCensusResult census) =>
            new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["dwg_ref"] = dwgRef,
                ["error"] = "aec_refused",
                ["message"] = string.IsNullOrEmpty(census.Drawing.AecWarning)
                    ? "this drawing holds only AutoCAD Architecture/Civil 3D/MEP objects, which cannot be read. " +
                      "Run EXPORTTOAUTOCAD in AutoCAD and attach the exported file."
                    : census.Drawing.AecWarning,
            };

        /// <summary>Levels with their elevations, and up to 80 device family types (electrical
        /// categories first) with how each one hosts: what a mapping may name.</summary>
        private static Dictionary<string, object?> ModelFacts(Document doc)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => (object?)new Dictionary<string, object?>
                {
                    ["name"] = l.Name,
                    ["elevation_mm"] = Math.Round(l.Elevation * MmPerFoot, 1),
                })
                .ToList();

            var model = new Dictionary<string, object?> { ["levels"] = levels };
            CadElectricalReview.AddFamilyTypes(model, DeviceTypes(doc).Select(fs => new ModelFamilyType
            {
                FamilyType = FullName(fs),
                Category = fs.Category?.Name ?? "",
                CategoryRank = CategoryRank(fs.Category),
                Hosting = HostingKind(fs.Family),
            }));
            model["conduit_types"] = RunTypeRows(CadElectricalResolver.ConduitTypes(doc), withSizes: true);
            model["cable_tray_types"] = RunTypeRows(CadElectricalResolver.TrayTypes(doc), withSizes: false);
            return model;
        }

        /// <summary>The category's place in <see cref="DeviceCategories"/>: electrical first.</summary>
        private static int CategoryRank(Category? category)
        {
            if (category == null) return DeviceCategories.Length;
            int rank = Array.IndexOf(DeviceCategories, (BuiltInCategory)category.Id.Value);
            return rank >= 0 ? rank : DeviceCategories.Length;
        }
        private const int MaxRunTypes = 50;

        /// <summary>Up to 50 conduit or cable tray types, with fittings and (conduit) trade sizes.</summary>
        private static List<object?> RunTypeRows(List<RunTypeInfo> types, bool withSizes) =>
            types.Take(MaxRunTypes).Select(t =>
            {
                var row = new Dictionary<string, object?>
                {
                    ["type"] = t.Name,
                    ["with_fittings"] = t.WithFittings,
                };
                if (withSizes) row["sizes_mm"] = t.SizesMm.Select(mm => (object?)Math.Round(mm, 1)).ToList();
                return (object?)row;
            }).ToList();

        // ─── shared with plan_cad_electrical and place_socket_points ───

        /// <summary>The family types of every device category in the model.</summary>
        internal static List<FamilySymbol> DeviceTypes(Document doc)
        {
            var filter = new ElementMulticategoryFilter(DeviceCategories.ToList());
            return new FilteredElementCollector(doc).WhereElementIsElementType()
                .OfClass(typeof(FamilySymbol)).WherePasses(filter)
                .Cast<FamilySymbol>()
                .ToList();
        }

        internal static string FullName(FamilySymbol fs) => fs.FamilyName + " : " + fs.Name;

        /// <summary>
        /// How a family hosts: wall | ceiling | floor | roof | face (from the family's Host
        /// setting), else work_plane | hosted | none from its placement type. A face or work
        /// plane family can go on any host; that is decided at placement.
        /// </summary>
        internal static string HostingKind(Family? family)
        {
            if (family == null) return "none";
            try
            {
                Parameter? p = family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR);
                if (p != null && p.StorageType == StorageType.Integer)
                {
                    switch (p.AsInteger())
                    {
                        case 1: return "wall";
                        case 2: return "floor";
                        case 3: return "ceiling";
                        case 4: return "roof";
                        case 5: return "face";
                    }
                }
            }
            catch { /* read the placement type instead */ }

            switch (family.FamilyPlacementType)
            {
                case FamilyPlacementType.OneLevelBasedHosted: return "hosted";
                case FamilyPlacementType.WorkPlaneBased: return "work_plane";
                default: return "none";
            }
        }

        // ─── the drawing ───────────────────────────────────────────────

        /// <summary>
        /// The file behind a dwg_ref. "att:&lt;guid&gt;" is a drawing the pane attached;
        /// "model:&lt;id&gt;" (or a bare id) is a CAD link in the model. An imported CAD lives
        /// inside the .rvt with no file on disk, and is refused with what to do instead.
        /// </summary>
        public static string ResolvePath(Document doc, string dwgRef)
        {
            if (string.IsNullOrWhiteSpace(dwgRef))
                throw new InvalidOperationException(
                    "dwg_ref is required — call list_cad_links, or use the ref from the [Attached DWG] block");

            string? path;
            if (DwgScratchCache.IsAttachmentRef(dwgRef))
            {
                path = DwgScratchCache.GetPath(dwgRef);
                if (string.IsNullOrEmpty(path))
                    throw new InvalidOperationException(
                        "unknown attachment " + dwgRef + " — attach the drawing again in the pane");
            }
            else
            {
                var raw = dwgRef.StartsWith("model:", StringComparison.OrdinalIgnoreCase)
                    ? dwgRef.Substring("model:".Length) : dwgRef;
                if (!long.TryParse(raw, out var id))
                    throw new InvalidOperationException(
                        "bad dwg_ref '" + dwgRef + "' — expected \"model:<id>\" or \"att:<guid>\"");
                if (doc.GetElement(ElemIds.From(id)) is not ImportInstance imp)
                    throw new InvalidOperationException(
                        "no CAD import with id " + id + " in this model — call list_cad_links for the current refs");

                path = DwgReader.PathOf(doc, imp);
                if (string.IsNullOrEmpty(path))
                    throw new InvalidOperationException(
                        "CAD " + id + " is imported into the model, so there is no drawing file to read. " +
                        "Link the DWG instead of importing it, or attach the file in the pane.");
            }

            if (!File.Exists(path))
                throw new InvalidOperationException(
                    "the drawing file for " + dwgRef + " is not on disk at " + path +
                    " — reload the link or attach the file again");
            return path!;
        }

        private sealed class CachedScan
        {
            public string Path = "";
            public long Length;
            public DateTime WriteUtc;
            public ElectricalScan Scan = null!;
            public DateTime LastUsed;
        }

        private static readonly List<CachedScan> _scans = new List<CachedScan>();

        /// <summary>The scan of a drawing, read once and kept (D12): two drawings, 30 minutes,
        /// keyed by path, length and last write time so a saved change is read again.</summary>
        public static ElectricalScan ScanFor(string path)
        {
            var info = new FileInfo(path);
            long length = info.Length;
            DateTime written = info.LastWriteTimeUtc;
            DateTime now = DateTime.UtcNow;

            _scans.RemoveAll(s => now - s.LastUsed > ScanTtl);
            CachedScan? hit = _scans.FirstOrDefault(s =>
                string.Equals(s.Path, info.FullName, StringComparison.OrdinalIgnoreCase) &&
                s.Length == length && s.WriteUtc == written);
            if (hit != null)
            {
                hit.LastUsed = now;
                return hit.Scan;
            }

            ElectricalScan scan = ElectricalCensus.ScanFile(info.FullName);
            _scans.RemoveAll(s => string.Equals(s.Path, info.FullName, StringComparison.OrdinalIgnoreCase));
            while (_scans.Count >= MaxScans)
            {
                CachedScan oldest = _scans.OrderBy(s => s.LastUsed).First();
                _scans.Remove(oldest);
            }
            _scans.Add(new CachedScan { Path = info.FullName, Length = length, WriteUtc = written, Scan = scan, LastUsed = now });
            return scan;
        }

        public static void CloseAll() => _scans.Clear();
    }
}
