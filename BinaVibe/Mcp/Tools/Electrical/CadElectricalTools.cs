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
            var modelByLevel = new Dictionary<string, IReadOnlyList<FitSegment>>(StringComparer.Ordinal);
            foreach (var pair in model.WallsByLevel)
                modelByLevel[pair.Key] = pair.Value.Select(w => new FitSegment(w.X1, w.Y1, w.X2, w.Y2)).ToList();

            // 4. The level.
            var fitOptions = new FitOptions();
            Level? level = null;
            FitResult? fit = null;
            string? levelName = ArgsHelp.GetString(args, "level")
                                ?? mapping.Plans.FirstOrDefault(p => p.Cluster == cluster && p.Level != null)?.Level;
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
                List<LevelFit> fits = CadModelFit.FitLevels(cad, modelByLevel, fitOptions, out bool tie);
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
            }

            // 5. The fit: centrelines, then faces. A positioned CAD link is not tried yet.
            List<ModelWall> walls = model.WallsByLevel.TryGetValue(level.Name, out List<ModelWall>? onLevel)
                ? onLevel : new List<ModelWall>();
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
                Dictionary<string, object?> failed = Failed(dwgRef, fit.FailReason ?? "alignment_failed",
                    "the drawing's walls could not be laid on the model's walls on " + level.Name +
                    " (score " + Math.Round(fit.Score, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "). Check the level and the background layers; or link the DWG into the model, move it " +
                    "onto the building, and run plan_cad_electrical again.");
                failed["level"] = level.Name;
                failed["cluster"] = cluster;
                failed["score"] = Math.Round(fit.Score, 3);
                failed["method"] = method;
                return failed;
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
            int otherPlans = 0;
            int total = scan.Symbols.Count;

            for (int i = 0; i < total; i++)
            {
                if (i % 200 == 0) McpProgress.Report(i, total);
                ScanSymbol symbol = scan.Symbols[i];
                if (!symbol.Placed) continue;
                rows.TryGetValue(symbol.Key, out SymbolRow? row);

                if (symbol.Cluster != cluster)
                {
                    if (row != null && CadElectricalMapping.IsPlaceable(row.Role)) otherPlans++;
                    continue;
                }

                fit.Transform.Apply(symbol.X, symbol.Y, out double mx, out double my);

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
                    Outcome = fit.Outcome,
                    Method = method,
                },
                ParamsUsed = new Dictionary<string, object?>
                {
                    ["dwg_ref"] = dwgRef,
                    ["cluster"] = cluster,
                    ["level"] = level.Name,
                    ["background_layers"] = background.Cast<object?>().ToList(),
                    ["pair_across_layers"] = pairAcross,
                    ["reach_mm"] = options.ReachMm,
                    ["axial_tolerance_mm"] = options.AxialTolMm,
                    ["existing_mm"] = options.ExistingMm,
                    ["duplicate_mm"] = options.DuplicateMm,
                    ["wet_room_keywords"] = options.WetKeywords.Cast<object?>().ToList(),
                },
            };
            string planId = SocketPlanCache.Store(plan, SocketCandidates.DocKey(doc));

            var notes = new List<string>(mapping.Notes);
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
                ["review"] = CadElectricalReview.PlanReview(plan, unmapped.Values.ToList(), otherPlans, notes),
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
