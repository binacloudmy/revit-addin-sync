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
        private const int MaxFamilyTypes = 200;
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

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dwg_ref"] = dwgRef,
                ["schema"] = Schema,
                ["census"] = CadElectricalReview.SlimCensus(census),
                ["roles"] = CadElectricalMapping.Roles.Cast<object?>().ToList(),
                ["hosts"] = CadElectricalMapping.Hosts.Cast<object?>().ToList(),
                ["layer_roles"] = CadElectricalMapping.LayerRoles.Cast<object?>().ToList(),
                ["model"] = ModelFacts(doc),
                ["notes"] = notes,
            };
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
            ElectricalScan scan = ScanFor(path);
            if (scan.Census.Drawing.AecRefused) return AecRefused(dwgRef, scan.Census);

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
            };
            mapping.Validate(facts, issues);
            if (issues.Count > 0) return MappingInvalid(dwgRef, issues);

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

            // 5. The fit: centrelines, then faces. A positioned CAD link is not tried yet.
            List<ModelWall> walls = structural.TryGetValue(level.Name, out List<ModelWall>? onLevel)
                ? onLevel : new List<ModelWall>();
            int thinIgnored = model.WallsByLevel.TryGetValue(level.Name, out List<ModelWall>? allOnLevel)
                ? allOnLevel.Count - walls.Count : 0;
            if (!modelByLevel.ContainsKey(level.Name)) modelByLevel[level.Name] = new List<FitSegment>();
            fit = fit ?? CadModelFit.Fit(cad, modelByLevel[level.Name], fitOptions);
            string method = "fit";
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
            if (fit.Outcome == "failed")
            {
                string message = fit.FailReason == "crop_too_small"
                    ? "too little of the drawing's wall linework lies on this level: check the background layers and the level"
                    : "the drawing's walls could not be laid on the model's walls on " + level.Name +
                      " (score " + Math.Round(fit.Score, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      "). Check the level and the background layers; or link the DWG into the model, move it " +
                      "onto the building, and run plan_cad_electrical again.";
                Dictionary<string, object?> failed = Failed(dwgRef, fit.FailReason ?? "alignment_failed", message);
                failed["level"] = level.Name;
                failed["cluster"] = cluster;
                failed["score"] = Math.Round(fit.Score, 3);
                failed["score_uncropped"] = Math.Round(fit.ScoreUncropped, 3);
                failed["inside_share"] = Math.Round(fit.InsideShare, 3);
                failed["method"] = method;
                return failed;
            }

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
                    return failed;
                }
            }

            // 6-7. Every placeable instance in the cluster, resolved to a point.
            var options = new ResolveOptions();
            var wallIndex = CadSymbolResolve.Index(walls);
            Func<double, double, RoomHit?> roomAt = CadElectricalResolver.RoomLookup(doc, level);
            double levelZMm = level.Elevation * MmPerFoot;
            bool provisional = fit.Outcome == "provisional";

            var rows = new Dictionary<string, SymbolRow>(StringComparer.Ordinal);
            foreach (SymbolRow row in mapping.Symbols) rows[row.Block] = row;
            var wallLike = new HashSet<string>(scan.WallLikeLayers, StringComparer.Ordinal);
            double symbolMax = new CensusOptions().SymbolMaxExtentMm;
            Func<string, bool> isMappedPlaceable = key =>
                rows.TryGetValue(key, out SymbolRow? r) && CadElectricalMapping.IsPlaceable(r.Role);

            var points = new List<PlannedPoint>();
            var unmapped = new Dictionary<string, UnmappedClass>(StringComparer.Ordinal);
            var dropped = new List<object?>();
            int otherPlans = 0, outsideFootprint = 0;
            int total = scan.Symbols.Count;
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
                if (membership != "plan")
                {
                    bool placeable = row != null && CadElectricalMapping.IsPlaceable(row.Role);
                    if (!placeable) continue;
                    if (membership == "other_plan")
                    {
                        otherPlans++;
                    }
                    else if (!CadSymbolResolve.NestedInMapped(scan.Symbols, i, isMappedPlaceable))
                    {
                        outsideFootprint++;
                        Drop(dropped, symbol.Key, membership, mx, my, levelZMm);
                    }
                    continue;
                }

                // Ignored classes, and unmapped ones that are not symbol-sized or sit on a wall
                // layer (containers, title blocks, the background's own blocks), say nothing.
                if (row != null && row.Role == "ignore") continue;
                if (row == null && (symbol.IsContainer || Math.Max(symbol.WidthMm, symbol.HeightMm) > symbolMax ||
                                    wallLike.Contains(symbol.Layer)))
                    continue;

                if (CadSymbolResolve.NestedInMapped(scan.Symbols, i, isMappedPlaceable))
                {
                    Drop(dropped, symbol.Key, "nested_in_mapped", mx, my, levelZMm);
                    continue;
                }

                string? reason = row == null ? "not_in_mapping" : CadElectricalMapping.UnplannedReason(row);
                if (reason != null)
                {
                    if (!unmapped.TryGetValue(symbol.Key, out UnmappedClass? u))
                        unmapped[symbol.Key] = u = new UnmappedClass { Block = symbol.Key, Reason = reason };
                    u.Instances++;
                    Drop(dropped, symbol.Key, reason, mx, my, levelZMm);
                    continue;
                }

                string host = row!.Host ?? CadElectricalMapping.DefaultHost(row.Role);
                PlannedPoint p = CadSymbolResolve.Resolve(
                    new ResolveInput { XMm = mx, YMm = my, Role = row.Role, Host = host, MountHeightMm = row.MountHeightMm },
                    walls, wallIndex, roomAt, levelZMm, model.ExistingDevices, options);
                p.Index = points.Count;
                p.LevelName = level.Name;
                p.SymbolKey = symbol.Key;
                p.FamilyType = row.FamilyType;
                if (provisional) CadSymbolResolve.AddFlag(p, "provisional_alignment");
                if (row.FamilyType != null && model.HostingByType.TryGetValue(row.FamilyType, out string? hosting) &&
                    CadSymbolResolve.HostMismatch(host, hosting))
                    CadSymbolResolve.AddFlag(p, "family_host_mismatch");
                points.Add(p);
            }
            McpProgress.Report(total, total);
            CadSymbolResolve.MarkDuplicates(points, options);

            // 8. Store, then review.
            var plan = new SocketPlan
            {
                Points = points,
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
                },
                ParamsUsed = new Dictionary<string, object?>
                {
                    ["dwg_ref"] = dwgRef,
                    ["cluster"] = cluster,
                    ["level"] = level.Name,
                    ["pick"] = pick >= 0 ? pick : (int?)null,
                    ["background_layers"] = background.Cast<object?>().ToList(),
                    ["pair_across_layers"] = pairAcross,
                    ["min_wall_mm"] = CadSymbolResolve.MinStructuralWallMm,
                    ["thin_walls_ignored"] = thinIgnored,
                    ["height_basis"] = "level",
                    ["reach_mm"] = options.ReachMm,
                    ["axial_tolerance_mm"] = options.AxialTolMm,
                    ["existing_mm"] = options.ExistingMm,
                    ["duplicate_mm"] = options.DuplicateMm,
                    ["wet_room_keywords"] = options.WetKeywords.Cast<object?>().ToList(),
                },
            };
            string planId = SocketPlanCache.Store(plan, SocketCandidates.DocKey(doc));

            var notes = new List<string>(mapping.Notes);
            notes.AddRange(fitNotes);
            if (ArgsHelp.GetDouble(args, "run_height_mm").HasValue)
                notes.Add("run_height_mm is for conduit runs, which come in phase 2; it was not used");
            if (provisional)
                notes.Add("the alignment is provisional (score under 0.80): check a few points before placing");

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["dwg_ref"] = dwgRef,
                ["level"] = level.Name,
                ["cluster"] = cluster,
                ["review"] = CadElectricalReview.PlanReview(plan, unmapped.Values.ToList(), otherPlans, notes, outsideFootprint),
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
                    ["facing"] = new List<object?> { Math.Round(p.FacingDx, 4), Math.Round(p.FacingDy, 4) },
                    ["flags"] = p.Flags.Cast<object?>().ToList(),
                    ["excluded"] = p.Excluded,
                }).ToList();
                result["points_truncated"] = points.Count > MaxPointRows;
                result["dropped"] = dropped.Take(MaxPointRows).ToList();
                result["dropped_truncated"] = dropped.Count > MaxPointRows;
            }
            return result;
        }

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
            var filter = new ElementMulticategoryFilter(DeviceCategories.ToList());
            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter))
            {
                if (!(el.Location is LocationPoint lp)) continue;
                var fi = el as FamilyInstance;
                Level? on = DeviceLevel(doc, el);
                if (level != null && (on == null || on.Id != level.Id)) continue;

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

            var dump = new Dictionary<string, object?>
            {
                ["schema"] = "cad.electrical.reference/1",
                ["level"] = level?.Name,
                ["devices"] = devices,
                ["links"] = links,
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

            File.WriteAllText(full, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["out_path"] = full,
                ["devices"] = devices.Count,
                ["links"] = links.Count,
                ["walls"] = wallCount,
            };
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

        private static void Drop(List<object?> dropped, string block, string reason, double x, double y, double z) =>
            dropped.Add(new Dictionary<string, object?>
            {
                ["block"] = block,
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

        /// <summary>Levels with their elevations, and up to 200 device family types with how
        /// each one hosts: what a mapping may name.</summary>
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

            List<FamilySymbol> types = DeviceTypes(doc)
                .OrderBy(fs => fs.Category?.Name ?? "", StringComparer.Ordinal)
                .ThenBy(fs => FullName(fs), StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new Dictionary<string, object?>
            {
                ["levels"] = levels,
                ["family_types"] = types.Take(MaxFamilyTypes).Select(fs => (object?)new Dictionary<string, object?>
                {
                    ["family_type"] = FullName(fs),
                    ["category"] = fs.Category?.Name ?? "",
                    ["hosting"] = HostingKind(fs.Family),
                }).ToList(),
                ["family_types_total"] = types.Count,
                ["family_types_truncated"] = types.Count > MaxFamilyTypes,
            };
        }

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
