// cad_windows_from_attachment — place Revit windows from an ATTACHED DWG's block data.
//
// Same pattern as CadDoorsFromAttachment:
// - Read window blocks via ACadSharp (block names, insertion points, rotations)
// - Find host walls at each insertion point
// - Map block names to window types
// - Place windows

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class CadWindowsFromAttachment
    {
        private const double MmPerFoot = 304.8;

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;

            // 1. Get attachment path and ACadSharp data
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref");
            if (string.IsNullOrEmpty(dwgRef))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "dwg_ref required" };

            var acadData = DwgScratchCache.GetAcadSharpData(dwgRef);
            if (acadData == null || !acadData.Ok)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"attachment '{dwgRef}' not found or ACadSharp extraction failed"
                };

            // 2. Filter window blocks
            var layerFilter = ArgsHelp.GetString(args, "layer_filter");
            var namePattern = ArgsHelp.GetString(args, "name_pattern") ?? "(?i)(window|win|tingkap|wdw)";

            var windowBlocks = FilterWindowBlocks(acadData.Blocks, layerFilter, namePattern);

            // 2b. Filter by Z range (for multi-floor DWGs)
            var zMinMm = ArgsHelp.GetDouble(args, "z_min_mm");
            var zMaxMm = ArgsHelp.GetDouble(args, "z_max_mm");
            if (zMinMm.HasValue || zMaxMm.HasValue)
            {
                windowBlocks = windowBlocks.Where(b =>
                {
                    if (zMinMm.HasValue && b.Z < zMinMm.Value) return false;
                    if (zMaxMm.HasValue && b.Z > zMaxMm.Value) return false;
                    return true;
                }).ToList();
            }

            if (windowBlocks.Count == 0)
            {
                var allNames = acadData.Blocks
                    .Select(b => b.Name)
                    .Distinct()
                    .Take(30)
                    .ToList();
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "no window blocks found matching filter",
                    ["layer_filter"] = layerFilter,
                    ["name_pattern"] = namePattern,
                    ["z_filter"] = zMinMm.HasValue || zMaxMm.HasValue ? $"{zMinMm ?? 0}mm to {zMaxMm ?? double.MaxValue}mm" : null,
                    ["available_block_names"] = allNames,
                };
            }

            // 3. Build census and detect sizes
            var census = BuildWindowCensus(windowBlocks);

            // 4. Get level
            var levelName = ArgsHelp.GetString(args, "level");
            Level? levelEl = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                levelEl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (levelEl == null)
                    return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"level '{levelName}' not found" };
            }
            else
            {
                levelEl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).FirstOrDefault();
                if (levelEl == null)
                    return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no levels in model" };
                levelName = levelEl.Name;
            }

            // 5. Get available window types
            var windowTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_Windows)
                .Cast<FamilySymbol>()
                .Select(fs => new { fs.Id, fs.Name, Family = fs.Family.Name })
                .ToList();

            // 6. Parse user-provided type mapping
            var typeMapping = ParseTypeMapping(args);

            // 7. Find walls at window positions (for host detection)
            var wallsAtLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w.LevelId == levelEl.Id)
                .ToList();

            // 8a. PREVIEW MODE
            if (ArgsHelp.GetBool(args, "create") != true)
            {
                // Suggest type mappings based on block names
                var suggestions = SuggestTypeMappings(census, windowTypes.Select(t => t.Name).ToList());

                // Sample window positions with wall detection
                var samples = windowBlocks.Take(20).Select(b =>
                {
                    var wall = FindHostWall(wallsAtLevel, b.X, b.Y);
                    return new Dictionary<string, object?>
                    {
                        ["block_name"] = b.Name,
                        ["x_mm"] = Math.Round(b.X, 1),
                        ["y_mm"] = Math.Round(b.Y, 1),
                        ["rotation_deg"] = b.Rotation,
                        ["host_wall_found"] = wall != null,
                        ["host_wall_id"] = wall?.Id.Value,
                    };
                }).ToList();

                int withHosts = samples.Count(s => (bool?)s["host_wall_found"] == true);

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["dwg_ref"] = dwgRef,
                    ["level"] = levelName,
                    ["window_count"] = windowBlocks.Count,
                    ["block_census"] = census,
                    ["suggested_mappings"] = suggestions,
                    ["available_window_types"] = windowTypes.Select(t => t.Name).Take(30).ToList(),
                    ["sample_positions"] = samples,
                    ["hosts_found_in_sample"] = $"{withHosts}/{samples.Count}",
                    ["walls_at_level"] = wallsAtLevel.Count,
                    ["note"] = "Preview only. Pass create=true with name_to_type mapping to place windows. "
                        + "name_to_type: {\"W900\": \"Fixed 900 x 1200mm\", ...}",
                };
            }

            // 8b. CREATE MODE
            if (typeMapping.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "create=true requires name_to_type mapping. Preview first to see suggested mappings."
                };

            // Get sill height (default 900mm from floor)
            double sillHeightMm = ArgsHelp.GetDouble(args, "sill_height_mm") ?? 900.0;
            double sillHeightFt = sillHeightMm / MmPerFoot;

            // Resolve type mapping to FamilySymbols
            var typeCache = new Dictionary<string, FamilySymbol?>(StringComparer.OrdinalIgnoreCase);
            FamilySymbol? ResolveType(string? name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                if (typeCache.TryGetValue(name, out var cached)) return cached;
                var fs = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                typeCache[name] = fs;
                return fs;
            }

            int created = 0, skippedNoType = 0, skippedNoWall = 0, failed = 0;
            string? firstError = null;
            var byType = new Dictionary<string, int>();

            using var tx = new Transaction(doc, "BinaVibe: cad_windows_from_attachment");
            TxGuard.StartSwallowing(tx);
            try
            {
                foreach (var block in windowBlocks)
                {
                    // Get type from mapping
                    if (!typeMapping.TryGetValue(block.Name, out var typeName) || string.IsNullOrEmpty(typeName))
                    {
                        skippedNoType++;
                        continue;
                    }

                    var windowType = ResolveType(typeName);
                    if (windowType == null)
                    {
                        skippedNoType++;
                        continue;
                    }

                    // Ensure type is activated
                    if (!windowType.IsActive)
                        windowType.Activate();

                    // Find host wall
                    var hostWall = FindHostWall(wallsAtLevel, block.X, block.Y);
                    if (hostWall == null)
                    {
                        skippedNoWall++;
                        continue;
                    }

                    try
                    {
                        // Convert CAD coords to Revit (assume CAD is mm)
                        var cadPoint = new XYZ(block.X / MmPerFoot, block.Y / MmPerFoot, 0);

                        // Project point onto wall centerline for proper placement
                        var locCurve = hostWall.Location as LocationCurve;
                        var wallCurve = locCurve?.Curve;
                        XYZ location;
                        if (wallCurve != null)
                        {
                            // Find nearest point on wall curve
                            var result = wallCurve.Project(cadPoint);
                            var onWall = result?.XYZPoint ?? cadPoint;

                            // CAD window insertion may be at corner of opening
                            // Revit window placement = center of opening
                            // Offset by half window width along wall direction
                            var windowWidthMm = DetectWidthFromName(block.Name)
                                ?? DetectWidthFromAttributes(block.Attributes)
                                ?? 1200; // default 1200mm if unknown
                            var halfWidthFt = (windowWidthMm / 2.0) / MmPerFoot;

                            // Get wall direction
                            var p0 = wallCurve.GetEndPoint(0);
                            var p1 = wallCurve.GetEndPoint(1);
                            var wallDir = (p1 - p0).Normalize();

                            // Offset toward wall center
                            var paramOnWall = result?.Parameter ?? 0.5;
                            var offsetDir = paramOnWall < 0.5 ? 1.0 : -1.0;

                            var centerOnWall = onWall + offsetDir * halfWidthFt * wallDir;
                            location = new XYZ(centerOnWall.X, centerOnWall.Y, sillHeightFt);
                        }
                        else
                        {
                            location = new XYZ(cadPoint.X, cadPoint.Y, sillHeightFt);
                        }

                        // Create window - Revit will orient it perpendicular to wall
                        var window = doc.Create.NewFamilyInstance(
                            location,
                            windowType,
                            hostWall,
                            levelEl,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                        created++;
                        byType[typeName] = byType.TryGetValue(typeName, out var n) ? n + 1 : 1;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        firstError ??= ex.Message;
                    }
                }
                TxGuard.CommitOrThrow(tx);
            }
            catch (Exception) { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["created"] = true,
                ["dwg_ref"] = dwgRef,
                ["level"] = levelName,
                ["window_blocks"] = windowBlocks.Count,
                ["created_count"] = created,
                ["by_type"] = byType,
                ["skipped_no_type_match"] = skippedNoType,
                ["skipped_no_host_wall"] = skippedNoWall,
                ["failed"] = failed,
                ["first_error"] = firstError,
                ["sill_height_mm"] = sillHeightMm,
            };
        }

        /// <summary>
        /// Filter blocks that look like windows (by layer or name pattern).
        /// </summary>
        private static List<CadBlock> FilterWindowBlocks(List<CadBlock> blocks, string? layerFilter, string namePattern)
        {
            var regex = new Regex(namePattern, RegexOptions.IgnoreCase);

            return blocks.Where(b =>
            {
                // Filter by layer if specified
                if (!string.IsNullOrEmpty(layerFilter))
                {
                    var layers = layerFilter.Split(',').Select(l => l.Trim()).ToList();
                    if (!layers.Any(l => b.Layer.IndexOf(l, StringComparison.OrdinalIgnoreCase) >= 0))
                        return false;
                }
                // Filter by name pattern
                return regex.IsMatch(b.Name);
            }).ToList();
        }

        /// <summary>
        /// Build census of window blocks with detected sizes.
        /// </summary>
        private static List<Dictionary<string, object?>> BuildWindowCensus(List<CadBlock> blocks)
        {
            return blocks
                .GroupBy(b => b.Name)
                .OrderByDescending(g => g.Count())
                .Select(g =>
                {
                    var sample = g.First();
                    var detectedWidth = DetectWidthFromName(sample.Name)
                        ?? DetectWidthFromAttributes(sample.Attributes);
                    var detectedHeight = DetectHeightFromAttributes(sample.Attributes);

                    return new Dictionary<string, object?>
                    {
                        ["name"] = g.Key,
                        ["count"] = g.Count(),
                        ["detected_width_mm"] = detectedWidth,
                        ["detected_height_mm"] = detectedHeight,
                        ["has_attributes"] = sample.Attributes.Count > 0,
                        ["attributes"] = sample.Attributes.Count > 0
                            ? sample.Attributes.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
                            : null,
                    };
                })
                .ToList();
        }

        /// <summary>
        /// Try to extract window width from block name (e.g., "W900" -> 900, "WIN1200" -> 1200).
        /// </summary>
        private static int? DetectWidthFromName(string name)
        {
            // Common patterns: W900, WIN1200, WINDOW_900, etc.
            var match = Regex.Match(name, @"(\d{3,4})");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var width))
            {
                // Sanity check: window widths are typically 400-3000mm
                if (width >= 300 && width <= 4000)
                    return width;
            }
            return null;
        }

        /// <summary>
        /// Try to extract window width from block attributes.
        /// </summary>
        private static int? DetectWidthFromAttributes(Dictionary<string, string> attrs)
        {
            var widthKeys = new[] { "WIDTH", "W", "LEBAR", "SIZE", "DIM" };
            foreach (var key in widthKeys)
            {
                if (attrs.TryGetValue(key, out var val))
                {
                    var match = Regex.Match(val, @"(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var width))
                    {
                        if (width >= 300 && width <= 4000)
                            return width;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Try to extract window height from block attributes.
        /// </summary>
        private static int? DetectHeightFromAttributes(Dictionary<string, string> attrs)
        {
            var heightKeys = new[] { "HEIGHT", "H", "TINGGI" };
            foreach (var key in heightKeys)
            {
                if (attrs.TryGetValue(key, out var val))
                {
                    var match = Regex.Match(val, @"(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var height))
                    {
                        if (height >= 300 && height <= 3000)
                            return height;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Suggest type mappings based on block names and available window types.
        /// </summary>
        private static Dictionary<string, string?> SuggestTypeMappings(
            List<Dictionary<string, object?>> census,
            List<string> availableTypes)
        {
            var suggestions = new Dictionary<string, string?>();

            foreach (var item in census)
            {
                var blockName = item["name"] as string ?? "";
                var detectedWidth = item["detected_width_mm"] as int?;

                string? bestMatch = null;

                if (detectedWidth.HasValue)
                {
                    // Try to find a window type containing this width
                    bestMatch = availableTypes.FirstOrDefault(t =>
                        t.Contains(detectedWidth.Value.ToString()));
                }

                if (bestMatch == null)
                {
                    // Try fuzzy match on block name parts
                    var nameParts = Regex.Split(blockName, @"[-_\s]+")
                        .Where(p => p.Length > 2)
                        .ToList();

                    foreach (var part in nameParts)
                    {
                        bestMatch = availableTypes.FirstOrDefault(t =>
                            t.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (bestMatch != null) break;
                    }
                }

                suggestions[blockName] = bestMatch;
            }

            return suggestions;
        }

        /// <summary>
        /// Find the wall that can host a window at the given CAD coordinate.
        /// Same logic as CadDoorsFromAttachment.
        /// </summary>
        private static Wall? FindHostWall(List<Wall> walls, double xMm, double yMm)
        {
            double xFt = xMm / MmPerFoot;
            double yFt = yMm / MmPerFoot;
            var point = new XYZ(xFt, yFt, 0);

            const double perpTolerance = 1.5; // ~450mm from centerline
            const double axialTolerance = 0.5; // ~150mm past endpoint

            Wall? best = null;
            double bestScore = double.MaxValue;

            foreach (var wall in walls)
            {
                var locCurve = wall.Location as LocationCurve;
                if (locCurve == null) continue;

                var curve = locCurve.Curve;
                if (curve == null) continue;

                var p0 = curve.GetEndPoint(0);
                var p1 = curve.GetEndPoint(1);

                var dir = (p1 - p0);
                double wallLen = dir.GetLength();
                if (wallLen < 0.01) continue;
                dir = dir.Normalize();

                var toDoor = point - p0;
                double axial = toDoor.DotProduct(dir);

                if (axial < -axialTolerance || axial > wallLen + axialTolerance)
                    continue;

                var perpVec = toDoor - axial * dir;
                double perp = perpVec.GetLength();

                if (perp > perpTolerance)
                    continue;

                double axialFromCenter = Math.Abs(axial - wallLen / 2);
                double score = perp * 10 + axialFromCenter * 0.1;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = wall;
                }
            }

            return best;
        }

        /// <summary>
        /// Parse user-provided name_to_type mapping.
        /// </summary>
        private static Dictionary<string, string> ParseTypeMapping(JsonElement args)
        {
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("name_to_type", out var obj)
                && obj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var typeName = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(typeName))
                            mapping[prop.Name] = typeName;
                    }
                }
            }

            return mapping;
        }
    }
}
