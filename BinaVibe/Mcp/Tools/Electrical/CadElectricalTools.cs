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
