using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>
    /// extract_cad_geometry — the ONE tested CAD reader for every CAD-derived
    /// function (blocks-to-family, beams, FF network, lighting).
    ///
    /// 19 rounds of CIDB testing (2026-07-15..19) produced ~15 distinct failure
    /// signatures, every one inside MODEL-GENERATED geometry-walking C#. This
    /// tool replaces that generated code with a single compiled, tested reader:
    /// the model calls it, gets structured JSON (imports, layer census,
    /// segments, block inserts, exploded-block clusters), and only writes
    /// trivial placement loops over the returned coordinates.
    ///
    /// Coordinates are returned in FEET (Revit internal — directly usable in
    /// new XYZ(...)); lengths/sizes additionally in mm for reporting.
    /// </summary>
    internal static class CadExtract
    {
        private static Dictionary<string, object?> Err(string message)
            => new Dictionary<string, object?> { ["ok"] = false, ["error"] = message };

        private const double EndpointTolFt = 0.033;   // ~10mm — connectivity clustering
        private const double MinSegmentFt = 0.164;    // ~50mm — below = block residue
        private const int MaxSegments = 2000;
        private const int MaxClusters = 200;

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;
            string? nameFilter = ArgsHelp.GetString(args, "name_filter");
            string? layerFilter = ArgsHelp.GetString(args, "layer_filter");
            long? importId = ArgsHelp.GetLong(args, "import_id");
            string? sourcePath = ArgsHelp.GetString(args, "source_path");

            // ── 1. collect + group imports by TYPE name ─────────────────────
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            if (all.Count == 0)
                return Err("no linked/imported CAD in the model");

            string NameOf(ImportInstance im)
                => doc.GetElement(im.GetTypeId())?.Name ?? im.Category?.Name ?? "(unnamed)";

            List<ImportInstance> candidates;
            if (importId.HasValue)
            {
                candidates = all.Where(im => im.Id.Value == importId.Value).ToList();
                if (candidates.Count == 0)
                    return Err($"import_id {importId} not found");
            }
            else if (!string.IsNullOrWhiteSpace(nameFilter))
            {
                candidates = all.Where(im =>
                    NameOf(im).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (candidates.Count == 0)
                {
                    var names = all.Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(20).ToList();
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["matched"] = 0,
                        ["imports_present"] = names,
                        ["note"] = "no import name contains '" + nameFilter + "' - pick from imports_present",
                    };
                }
            }
            else
            {
                candidates = all;
            }

            // ── 2. choose ONE instance: prefer the active view's level ──────
            ImportInstance chosen;
            if (candidates.Count == 1)
            {
                chosen = candidates[0];
            }
            else
            {
                var activeLevelId = (uidoc.ActiveView as ViewPlan)?.GenLevel?.Id
                                    ?? uidoc.ActiveView?.GenLevel?.Id;
                var onActive = activeLevelId != null
                    ? candidates.Where(im => im.LevelId == activeLevelId).ToList()
                    : new List<ImportInstance>();
                if (onActive.Count == 1)
                {
                    chosen = onActive[0];
                }
                else
                {
                    // ambiguous — DO NOT guess; report instances so the model asks the user
                    var options = candidates.Take(15).Select(im => (object)new Dictionary<string, object?>
                    {
                        ["import_id"] = im.Id.Value,
                        ["name"] = NameOf(im),
                        ["level"] = (doc.GetElement(im.LevelId) as Level)?.Name ?? "(none)",
                    }).ToList();
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["matched"] = candidates.Count,
                        ["ambiguous"] = true,
                        ["instances"] = options,
                        ["note"] = "multiple matching CAD instances - call again with import_id after asking the user",
                    };
                }
            }

            // ── 3. walk the chosen instance's geometry ──────────────────────
            var layerCensus = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase); // [gi, pl, ln, arc, other]
            var segments = new List<Dictionary<string, object>>();
            var blocks = new List<Dictionary<string, object>>();
            var circles = new List<Dictionary<string, object>>();
            var clusterCurves = new List<Curve>();
            bool truncated = false;
            int skippedEntities = 0;

            string LayerOf(GeometryObject o)
                => (doc.GetElement(o.GraphicsStyleId) as GraphicsStyle)?
                       .GraphicsStyleCategory?.Name ?? "(no layer)";

            void Bump(string layer, int slot)
            {
                if (!layerCensus.TryGetValue(layer, out var c))
                    layerCensus[layer] = c = new int[5];
                c[slot]++;
            }

            bool LayerWanted(string layer)
                => string.IsNullOrWhiteSpace(layerFilter)
                   || layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) >= 0;

            void AddSegment(XYZ a, XYZ b, string layer)
            {
                if (a.DistanceTo(b) < MinSegmentFt) return;
                if (segments.Count >= MaxSegments) { truncated = true; return; }
                segments.Add(new Dictionary<string, object>
                {
                    ["x1_ft"] = Math.Round(a.X, 4), ["y1_ft"] = Math.Round(a.Y, 4), ["z1_ft"] = Math.Round(a.Z, 4),
                    ["x2_ft"] = Math.Round(b.X, 4), ["y2_ft"] = Math.Round(b.Y, 4), ["z2_ft"] = Math.Round(b.Z, 4),
                    // mm twins: exact arg shape for mm-based tools (create_beam/create_pipe).
                    // Round-28 root cause: the model converted 19.685 ft with x30.48 (cm
                    // factor) and every beam came out 1/10 scale — no model-side unit
                    // conversion is trustworthy, so ship both unit systems ready-made.
                    ["start_mm"] = new[] { Math.Round(a.X * 304.8, 1), Math.Round(a.Y * 304.8, 1), Math.Round(a.Z * 304.8, 1) },
                    ["end_mm"] = new[] { Math.Round(b.X * 304.8, 1), Math.Round(b.Y * 304.8, 1), Math.Round(b.Z * 304.8, 1) },
                    ["length_mm"] = Math.Round(a.DistanceTo(b) * 304.8, 1),
                    ["layer"] = layer,
                });
            }

            void WalkNested(GeometryInstance gi, int depth)
            {
                foreach (GeometryObject obj in gi.GetInstanceGeometry())
                {
                    try
                    {
                    var layer = LayerOf(obj);
                    if (obj is GeometryInstance nested)
                    {
                        Bump(layer, 0);
                        var sym = doc.GetElement(nested.GetSymbolGeometryId().SymbolId);
                        var tf = nested.Transform;
                        var rot = Math.Atan2(tf.BasisX.Y, tf.BasisX.X) * 180.0 / Math.PI;
                        if (LayerWanted(layer) && blocks.Count < MaxClusters)
                        {
                            blocks.Add(new Dictionary<string, object>
                            {
                                ["block_name"] = sym?.Name ?? "(unnamed)",
                                ["x_ft"] = Math.Round(tf.Origin.X, 4),
                                ["y_ft"] = Math.Round(tf.Origin.Y, 4),
                                ["z_ft"] = Math.Round(tf.Origin.Z, 4),
                                ["point_mm"] = new[] { Math.Round(tf.Origin.X * 304.8, 1), Math.Round(tf.Origin.Y * 304.8, 1), Math.Round(tf.Origin.Z * 304.8, 1) },
                                ["rotation_deg"] = Math.Round(rot, 1),
                                ["mirrored"] = tf.HasReflection,
                                ["layer"] = layer,
                            });
                        }
                        if (depth < 1) WalkNested(nested, depth + 1);
                    }
                    else if (obj is PolyLine pl)
                    {
                        Bump(layer, 1);
                        if (!LayerWanted(layer)) continue;
                        var pts = pl.GetCoordinates();
                        for (int i = 0; i + 1 < pts.Count; i++)
                        {
                            AddSegment(pts[i], pts[i + 1], layer);
                            if (pts[i].DistanceTo(pts[i + 1]) >= MinSegmentFt)
                                clusterCurves.Add(Line.CreateBound(pts[i], pts[i + 1]));
                        }
                    }
                    else if (obj is Line ln)
                    {
                        Bump(layer, 2);
                        if (!LayerWanted(layer)) continue;
                        if (!ln.IsBound) continue;   // ray/xline — no endpoints
                        AddSegment(ln.GetEndPoint(0), ln.GetEndPoint(1), layer);
                        if (ln.Length >= MinSegmentFt) clusterCurves.Add(ln);
                    }
                    else if (obj is Arc arc)
                    {
                        Bump(layer, 3);
                        if (!LayerWanted(layer)) continue;
                        if (!arc.IsBound)
                        {
                            // Full circle — a point marker (sprinkler heads etc.).
                            // Calling GetEndPoint on it throws "The input curve is
                            // not bound" (round-20 CIDB failure, all 15 instances).
                            if (circles.Count < MaxClusters)
                                circles.Add(new Dictionary<string, object>
                                {
                                    ["x_ft"] = Math.Round(arc.Center.X, 4),
                                    ["y_ft"] = Math.Round(arc.Center.Y, 4),
                                    ["z_ft"] = Math.Round(arc.Center.Z, 4),
                                    ["point_mm"] = new[] { Math.Round(arc.Center.X * 304.8, 1), Math.Round(arc.Center.Y * 304.8, 1), Math.Round(arc.Center.Z * 304.8, 1) },
                                    ["radius_mm"] = Math.Round(arc.Radius * 304.8, 1),
                                    ["layer"] = layer,
                                });
                        }
                        else clusterCurves.Add(arc);
                    }
                    else
                    {
                        Bump(layer, 4);
                    }
                    }
                    catch { skippedEntities++; }
                }
            }

            var geo = chosen.get_Geometry(new Options());
            if (geo == null)
                return Err("chosen import has no readable geometry");
            foreach (GeometryObject top in geo)
                if (top is GeometryInstance giTop) WalkNested(giTop, 0);

            // ── 4. exploded-block clusters: union-find on shared endpoints ──
            var clusters = BuildClusters(clusterCurves);

            // dedupe segments (both endpoints rounded ~1mm, order-normalised)
            var seen = new HashSet<string>();
            var deduped = new List<Dictionary<string, object>>();
            foreach (var s in segments)
            {
                string k1 = s["x1_ft"] + "," + s["y1_ft"] + "," + s["z1_ft"];
                string k2 = s["x2_ft"] + "," + s["y2_ft"] + "," + s["z2_ft"];
                var key = string.CompareOrdinal(k1, k2) <= 0 ? k1 + "|" + k2 : k2 + "|" + k1;
                if (seen.Add(key)) deduped.Add(s);
            }

            var census = layerCensus.OrderByDescending(kv => kv.Value.Sum())
                .Take(20)
                .Select(kv => (object)new Dictionary<string, object?>
                {
                    ["layer"] = kv.Key,
                    ["geometry_instances"] = kv.Value[0],
                    ["polylines"] = kv.Value[1],
                    ["lines"] = kv.Value[2],
                    ["arcs"] = kv.Value[3],
                    ["other"] = kv.Value[4],
                }).ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["import_id"] = chosen.Id.Value,
                ["import_name"] = NameOf(chosen),
                ["import_level"] = (doc.GetElement(chosen.LevelId) as Level)?.Name ?? "(none)",
                ["instances_of_this_type"] = candidates.Count,
                ["layer_census"] = census,
                ["segments"] = deduped,
                ["segments_found"] = segments.Count,
                ["segments_unique"] = deduped.Count,
                ["blocks"] = blocks,
                ["circles"] = circles,
                ["clusters"] = clusters,
                ["truncated"] = truncated,
                ["skipped_entities"] = skippedEntities,
                ["import_location"] = ImportLocation(doc, chosen),
                // TEXT/MTEXT/ATTRIB read straight from the source FILE — the
                // Revit API cannot expose CAD text (TextNode is dead in 2026),
                // which blocked DEV-16's per-size beam mapping for the whole
                // campaign. The source file can: parse it directly.
                ["text_labels"] = ReadLinkedCadTextLabels(doc, chosen, sourcePath),
                ["units_note"] = "*_ft fields are FEET (use directly in C# XYZ); start_mm/end_mm/point_mm are MILLIMETER triplets — pass them UNCHANGED as the *_mm args of mm-based tools (create_beam, create_pipe, place_family). NEVER convert units yourself: ft to mm is x304.8 and a x30.48 slip has already shipped 1/10-scale beams.",
            };
        }

        // TEXT/MTEXT/ATTRIB entities parsed from a LINKED or IMPORTED CAD's
        // source file. The Revit API gives no access to CAD text, but the file on
        // disk does. Linked CAD has an ExternalFileReference. Imported CAD is
        // embedded and has none, so we look for the original file next to the
        // .rvt (or in a known folder), and accept an explicit source_path from
        // the agent when the user can point at it.
        //
        // Label points are converted from file units to feet, then run through
        // the instance's total transform. That puts them in the same model space
        // as segment/point *_mm, so a nearest-segment match works for links that
        // are rotated, scaled, or not at the origin.
        private static Dictionary<string, object?> ReadLinkedCadTextLabels(
            Document doc, ImportInstance im, string? sourcePathOverride)
        {
            try
            {
                string path;
                string sourceKind;

                bool isExternal;
                try { isExternal = ExternalFileUtils.IsExternalFileReference(doc, im.GetTypeId()); }
                catch { isExternal = false; }

                if (!string.IsNullOrWhiteSpace(sourcePathOverride))
                {
                    if (!System.IO.File.Exists(sourcePathOverride))
                        return Unavailable(doc, im,
                            "source_path was not found: " + sourcePathOverride +
                            ". Point it at the original DWG/DXF, or link the file instead of importing it.");
                    path = sourcePathOverride;
                    sourceKind = "source_path";
                }
                else if (isExternal)
                {
                    path = null;
                    var extRef = ExternalFileUtils.GetExternalFileReference(doc, im.GetTypeId());
                    if (extRef != null)
                        path = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
                    if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                        return Unavailable(doc, im,
                            "the linked CAD source file was not found on disk" +
                            (string.IsNullOrWhiteSpace(path) ? "" : " (" + path + ")") +
                            ". Re-link it, or call extract_cad_geometry with source_path set to the original DWG/DXF.");
                    sourceKind = "linked";
                }
                else
                {
                    path = FindImportedCadFile(doc, im);
                    if (path == null)
                        return Unavailable(doc, im,
                            "labels are not available: this CAD was imported (embedded in the model) and no source file was found next to the .rvt. " +
                            "Link the DWG instead of importing it, or call extract_cad_geometry with source_path set to the original DWG/DXF.");
                    sourceKind = "imported (found on disk)";
                }

                var read = CadFileReader.ReadText(path);
                if (!read.Ok)
                    return new Dictionary<string, object?>
                    {
                        ["available"] = false,
                        ["source_file"] = path,
                        ["source_kind"] = sourceKind,
                        ["note"] = "could not read the CAD source: " + read.Error +
                                   ". Link the DWG instead of importing it, or call extract_cad_geometry with source_path set to a readable DWG/DXF.",
                    };

                Transform? total = null;
                try { total = im.GetTotalTransform(); } catch { /* file coordinates only */ }

                var labels = new List<Dictionary<string, object?>>();
                bool truncated = false;
                foreach (var t in read.Texts)
                {
                    if (string.IsNullOrWhiteSpace(t.Content)) continue;
                    if (labels.Count >= 200) { truncated = true; break; }
                    labels.Add(Label(t, read.UnitToMm, total));
                }

                string unitsText = read.UnitsKnown
                    ? "header ($INSUNITS) -> " + read.UnitToMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " mm/unit"
                    : read.UnitsInferred
                        ? "inferred from drawing extents -> " + read.UnitToMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " mm/unit"
                        : "unknown (assumed mm)";

                var note = "read from the " + sourceKind + " source file (Revit API cannot see CAD text). " +
                           "Labels include TEXT, MTEXT, ATTRIB and text nested in INSERT blocks. " +
                           "x_mm/y_mm/z_mm and x_ft/y_ft/z_ft are MODEL coordinates: file units were converted to feet and the CAD instance's total transform was applied, " +
                           "so each label lines up with the segment/point *_mm fields. Match each label to its nearest segment.";
                if (read.UnitsInferred)
                    note = "the file declares no units - the scale was " + unitsText + ". " + note;
                if (total == null)
                    note = "could not read the CAD placement transform; x_mm/y_mm are file coordinates, not model coordinates. " + note;
                if (!string.IsNullOrEmpty(read.SourceWarning))
                    note = read.SourceWarning + " " + note;

                return new Dictionary<string, object?>
                {
                    ["available"] = true,
                    ["labels"] = labels,
                    ["count"] = labels.Count,
                    ["truncated"] = truncated,
                    ["source_file"] = path,
                    ["source_kind"] = sourceKind,
                    ["file_units"] = unitsText,
                    ["file_mm_per_unit"] = Math.Round(read.UnitToMm, 6),
                    ["model_space"] = total != null,
                    ["note"] = note,
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?> { ["available"] = false, ["note"] = "text parse failed: " + ex.Message };
            }
        }

        private static Dictionary<string, object?> Label(CadText t, double unitToMm, Transform? total)
        {
            double xMm = t.X * unitToMm;
            double yMm = t.Y * unitToMm;
            double zMm = t.Z * unitToMm;
            if (total != null)
            {
                var modelFt = total.OfPoint(new XYZ(xMm / 304.8, yMm / 304.8, zMm / 304.8));
                xMm = modelFt.X * 304.8;
                yMm = modelFt.Y * 304.8;
                zMm = modelFt.Z * 304.8;
            }
            return new Dictionary<string, object?>
            {
                ["text"] = t.Content.Trim(),
                ["type"] = t.Type,
                ["x_mm"] = Math.Round(xMm, 1),
                ["y_mm"] = Math.Round(yMm, 1),
                ["z_mm"] = Math.Round(zMm, 1),
                ["x_ft"] = Math.Round(xMm / 304.8, 4),
                ["y_ft"] = Math.Round(yMm / 304.8, 4),
                ["z_ft"] = Math.Round(zMm / 304.8, 4),
                ["layer"] = string.IsNullOrEmpty(t.Layer) ? "0" : t.Layer,
            };
        }

        private static Dictionary<string, object?> Unavailable(Document doc, ImportInstance im, string note)
        {
            var fileName = SourceFileName(doc, im);
            return new Dictionary<string, object?>
            {
                ["available"] = false,
                ["source_file_name"] = fileName,
                ["note"] = note +
                    (string.IsNullOrWhiteSpace(fileName) ? "" : " (expected file: " + fileName + ")"),
            };
        }

        private static string? SourceFileName(Document doc, ImportInstance im)
        {
            try { return doc.GetElement(im.GetTypeId())?.Name; }
            catch { return null; }
        }

        // Imported CAD keeps the original file name (e.g. "Drawing1.dwg") on
        // its CADLinkType but has no path. Look for it beside the .rvt and in
        // the usual user folders. This is a best effort: if the file was moved
        // or renamed, the caller gets the actionable note from Unavailable.
        private static string? FindImportedCadFile(Document doc, ImportInstance im)
        {
            var fileName = SourceFileName(doc, im);
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            fileName = System.IO.Path.GetFileName(fileName);

            var names = new List<string> { fileName };
            if (string.IsNullOrEmpty(System.IO.Path.GetExtension(fileName)))
            {
                names.Add(fileName + ".dwg");
                names.Add(fileName + ".dxf");
            }

            var folders = new List<string?>();
            try
            {
                var docPath = doc.PathName;
                if (!string.IsNullOrWhiteSpace(docPath))
                    folders.Add(System.IO.Path.GetDirectoryName(docPath));
            }
            catch { /* unsaved/cloud model */ }
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            folders.Add(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            folders.Add(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"));

            var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !searched.Add(folder)) continue;
                foreach (var name in names)
                {
                    try
                    {
                        var candidate = System.IO.Path.Combine(folder, name);
                        if (System.IO.File.Exists(candidate)) return candidate;
                    }
                    catch { /* invalid path segment */ }
                }
            }
            return null;
        }

        // Where the chosen CAD instance actually sits — placements land HERE.
        // Round-20 CIDB: 5 rounds of "wrong location" reports were placements
        // at the import's true position while the tester checked another view.
        private static Dictionary<string, object?> ImportLocation(Document doc, ImportInstance im)
        {
            try
            {
                var bb = im.get_BoundingBox(null);
                if (bb == null) return new Dictionary<string, object?> { ["known"] = false };
                var c = (bb.Min + bb.Max) * 0.5;
                return new Dictionary<string, object?>
                {
                    ["known"] = true,
                    ["center_x_ft"] = Math.Round(c.X, 2),
                    ["center_y_ft"] = Math.Round(c.Y, 2),
                    ["center_z_ft"] = Math.Round(c.Z, 2),
                    ["level"] = (doc.GetElement(im.LevelId) as Level)?.Name ?? "(none)",
                    ["note"] = "placements will land at this location - verify in a view showing it",
                };
            }
            catch { return new Dictionary<string, object?> { ["known"] = false }; }
        }

        // Connected-component clustering: curves sharing an endpoint (within
        // ~10mm) belong to one exploded block. Radius-free — follows topology.
        private static List<object> BuildClusters(List<Curve> curves)
        {
            int n = curves.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }

            for (int i = 0; i < n; i++)
            {
                if (!curves[i].IsBound) continue;
                var a0 = curves[i].GetEndPoint(0); var a1 = curves[i].GetEndPoint(1);
                for (int j = i + 1; j < n; j++)
                {
                    if (!curves[j].IsBound) continue;
                    var b0 = curves[j].GetEndPoint(0); var b1 = curves[j].GetEndPoint(1);
                    if (a0.DistanceTo(b0) < EndpointTolFt || a0.DistanceTo(b1) < EndpointTolFt ||
                        a1.DistanceTo(b0) < EndpointTolFt || a1.DistanceTo(b1) < EndpointTolFt)
                        parent[Root(i)] = Root(j);
                }
            }

            var groups = new Dictionary<int, List<Curve>>();
            for (int i = 0; i < n; i++)
            {
                int r = Root(i);
                if (!groups.TryGetValue(r, out var list)) groups[r] = list = new List<Curve>();
                list.Add(curves[i]);
            }

            var result = new List<object>();
            foreach (var grp in groups.Values)
            {
                if (result.Count >= MaxClusters) break;
                double x = 0, y = 0, z = 0; int cnt = 0;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                Curve longest = grp[0];
                foreach (var c in grp)
                {
                    if (!c.IsBound) continue;
                    var m = c.Evaluate(0.5, true);
                    x += m.X; y += m.Y; z += m.Z; cnt++;
                    foreach (var p in new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                    {
                        if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                        if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                    }
                    if (c.Length > longest.Length) longest = c;
                }
                var dir = (longest.GetEndPoint(1) - longest.GetEndPoint(0)).Normalize();
                result.Add(new Dictionary<string, object?>
                {
                    ["x_ft"] = Math.Round(x / cnt, 4),
                    ["y_ft"] = Math.Round(y / cnt, 4),
                    ["z_ft"] = Math.Round(z / cnt, 4),
                    ["point_mm"] = new[] { Math.Round(x / cnt * 304.8, 1), Math.Round(y / cnt * 304.8, 1), Math.Round(z / cnt * 304.8, 1) },
                    ["curve_count"] = cnt,
                    ["size_x_mm"] = Math.Round((maxX - minX) * 304.8, 0),
                    ["size_y_mm"] = Math.Round((maxY - minY) * 304.8, 0),
                    ["rotation_deg"] = Math.Round(Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI, 1),
                });
            }
            return result;
        }
    }
}
