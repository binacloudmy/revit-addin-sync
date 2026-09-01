using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>
    /// batch_link_models — the ONE tested Batch Link (CIDB #4 / DEV-04).
    ///
    /// Rounds 39-50 tried this as model-generated C# and produced a new failure
    /// every round: LoadResultCode missing, GetLinkFilePaths missing, ModelPath-
    /// to-string at 3 sites, HashSet<string> indexing, silent "0 linked / 2
    /// failed" with no reason, catch-all AR rule sweeping the host's own recovery
    /// copies. The scan+match+link+verify is deterministic, so — like
    /// compare_levels and extract_cad_geometry — it becomes a compiled tool. The
    /// model calls it and renders the structured result; it writes no link C#.
    ///
    /// Discipline matching is POSITIVE-evidence only (an AR/ST/ACMV token in the
    /// file name), the host document and its backup/recovery copies are excluded,
    /// the scan descends ONE subfolder level, each Create is verified by a
    /// RevitLinkInstance collector delta, and every per-file outcome carries its
    /// LoadResult / exception reason.
    /// </summary>
    internal static class BatchLink
    {
        private static Dictionary<string, object?> Err(string message)
            => new Dictionary<string, object?> { ["ok"] = false, ["error"] = message };

        private static readonly string[] StTokens = { "_ST_", "STRUCT", "STRUKTUR", "-ST-", " ST " };
        private static readonly string[] AcmvTokens = { "ACMV", "_ME_", "MECH", "-ME-", " ME " };
        private static readonly string[] ArTokens = { "_AR_", "ARCHITECT", "SENIBINA", "-AR-", " AR " };

        private static string DisciplineOf(string fileName)
        {
            var u = fileName.ToUpperInvariant();
            if (StTokens.Any(t => u.Contains(t))) return "ST";
            if (AcmvTokens.Any(t => u.Contains(t))) return "ACMV";
            if (ArTokens.Any(t => u.Contains(t))) return "AR";
            return "";   // no positive evidence → not a candidate
        }

        // Host document + its Revit auto-backups (Name.NNNN.rvt) and recovery
        // copies must never be link candidates (round 40: 7 self-link attempts).
        private static bool IsHostOrBackup(string path, string hostTitle)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(name, hostTitle, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(hostTitle, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.IndexOf("(Recovery)", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // trailing .NNNN backup suffix
            var dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1 && name.Substring(dot + 1).All(char.IsDigit)) return true;
            return false;
        }

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;
            string? folder = ArgsHelp.GetString(args, "folder");
            bool pin = ArgsHelp.GetBool(args, "pin") ?? true;

            if (string.IsNullOrWhiteSpace(folder))
            {
                var docPath = doc.PathName;
                if (string.IsNullOrWhiteSpace(docPath))
                    return Err("no folder given and the host model is unsaved — provide an explicit folder path");
                folder = Path.GetDirectoryName(docPath);
            }
            if (!Directory.Exists(folder))
                return Err($"folder not found: {folder}");

            string hostTitle = doc.Title;
            var scanned = new List<string> { folder };

            // ── 1. scan the folder + one subfolder level for .rvt candidates ─
            var files = new List<string>();
            try { files.AddRange(Directory.GetFiles(folder!, "*.rvt", SearchOption.TopDirectoryOnly)); }
            catch (Exception ex) { return Err($"cannot read folder: {ex.Message}"); }
            foreach (var subDir in SafeDirs(folder!))
            {
                scanned.Add(subDir);
                try { files.AddRange(Directory.GetFiles(subDir, "*.rvt", SearchOption.TopDirectoryOnly)); }
                catch { /* skip unreadable subfolder */ }
            }

            // ── 2. classify: positive discipline, exclude host + backups ─────
            var candidates = new List<(string Path, string Disc)>();
            var otherRvt = new List<string>();
            foreach (var f in files.Distinct())
            {
                if (IsHostOrBackup(f, hostTitle)) continue;
                var disc = DisciplineOf(Path.GetFileName(f));
                if (disc == "") { otherRvt.Add(Path.GetFileName(f)); continue; }
                candidates.Add((f, disc));
            }
            // keep at most one candidate per discipline — the newest file
            candidates = candidates
                .GroupBy(c => c.Disc)
                .Select(g => g.OrderByDescending(c => SafeWriteTime(c.Path)).First())
                .ToList();

            int arN = candidates.Count(c => c.Disc == "AR");
            int stN = candidates.Count(c => c.Disc == "ST");
            int acmvN = candidates.Count(c => c.Disc == "ACMV");

            if (candidates.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created"] = 0, ["reloaded"] = 0, ["failed"] = 0, ["skipped"] = 0,
                    ["ar_candidates"] = 0, ["st_candidates"] = 0, ["acmv_candidates"] = 0,
                    ["scanned_folders"] = scanned,
                    ["other_rvt_in_folder"] = otherRvt,
                    ["details"] = new List<string>(),
                    ["headline"] = $"No AR/ST/ACMV candidates in {folder} (+{scanned.Count - 1} subfolder). "
                        + (otherRvt.Count > 0 ? $"{otherRvt.Count} other .rvt present (no discipline token). " : "")
                        + "Point me to the correct folder?",
                };

            // existing links (name → type) for reload/skip decisions
            var existingTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();

            int created = 0, reloaded = 0, failed = 0, skipped = 0;
            var details = new List<string>();
            string? firstFailReason = null;

            // ── 3. link each candidate (Create outside tx, place inside) ─────
            foreach (var (path, disc) in candidates)
            {
                var fileName = Path.GetFileName(path);
                var outcome = LinkOneRvt(doc, path, pin, existingTypes);
                switch (outcome.Status)
                {
                    case "reloaded":
                        reloaded++;
                        details.Add($"{fileName} [{disc}] -> RELOADED (already linked)");
                        break;
                    case "created":
                        created++;
                        details.Add($"{fileName} [{disc}] -> CREATED (origin-to-origin{(pin ? ", pinned" : "")})");
                        break;
                    default:
                        failed++;
                        firstFailReason ??= $"{fileName}: {outcome.Reason}";
                        details.Add($"{fileName} [{disc}] -> FAILED ({outcome.Reason})");
                        break;
                }
            }

            string headline =
                $"Searched {folder} (+{scanned.Count - 1} subfolder) — AR:{arN} ST:{stN} ACMV:{acmvN} | "
                + $"created {created}, reloaded {reloaded}, failed {failed}, skipped {skipped}";
            string sub = failed > 0 && firstFailReason != null
                ? "First failure: " + firstFailReason
                : "origin-to-origin" + (pin ? ", pinned" : "");

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["created"] = created, ["reloaded"] = reloaded,
                ["failed"] = failed, ["skipped"] = skipped,
                ["ar_candidates"] = arN, ["st_candidates"] = stN, ["acmv_candidates"] = acmvN,
                ["scanned_folders"] = scanned,
                ["other_rvt_in_folder"] = otherRvt,
                ["details"] = details,
                ["first_failure"] = firstFailReason,
                ["headline"] = headline,
                ["sub"] = sub,
            };
        }

        // ─── shared RVT link+verify (used by batch_link_models AND link_file) ──
        // Extracted verbatim from the batch loop above so link_file's .rvt arm
        // never re-derives its own Create/verify sequence — the same
        // "no ambient transaction" lesson (round 52) and collector-delta
        // verification apply to a single file exactly as they do to a batch.
        private sealed class RvtLinkOutcome
        {
            public string Status = "failed";   // "created" | "reloaded" | "failed"
            public string? Reason;
            public ElementId? ElementId;
        }

        private static RvtLinkOutcome LinkOneRvt(Document doc, string path, bool pin, List<RevitLinkType> existingTypes)
        {
            var fileName = Path.GetFileName(path);
            try
            {
                var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
                // already linked by this file name? reload instead of re-create
                var existing = existingTypes.FirstOrDefault(t =>
                    string.Equals(t.Name, fileName, StringComparison.OrdinalIgnoreCase)
                    || (t.Name?.IndexOf(Path.GetFileNameWithoutExtension(path),
                          StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
                if (existing != null)
                {
                    existing.Reload();
                    return new RvtLinkOutcome { Status = "reloaded", ElementId = existing.Id };
                }

                int before = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).GetElementCount();
                // The tool executor runs us WITHOUT an ambient transaction, and
                // in that context RevitLinkType.Create throws "Modifying is
                // forbidden because the document has no open transaction"
                // (round 52 — the tool surfaced its own bug via the failure
                // reason). Both Create and the instance placement go inside
                // ONE transaction.
                LinkLoadResult res;
                RevitLinkInstance? inst = null;
                using (var tx = new Transaction(doc, $"BinaVibe: link {fileName}"))
                {
                    tx.Start();
                    res = RevitLinkType.Create(doc, mp, new RevitLinkOptions(false));
                    if (res.LoadResult != LinkLoadResultType.LinkLoaded)
                    {
                        tx.RollBack();
                        return new RvtLinkOutcome { Status = "failed", Reason = res.LoadResult.ToString() };
                    }
                    inst = RevitLinkInstance.Create(doc, res.ElementId);
                    if (pin && inst != null) inst.Pinned = true;
                    tx.Commit();
                }
                int after = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).GetElementCount();
                if (after > before)
                    return new RvtLinkOutcome { Status = "created", ElementId = inst?.Id ?? res.ElementId };
                return new RvtLinkOutcome { Status = "failed", Reason = "created type but no instance appeared" };
            }
            catch (Exception ex)
            {
                return new RvtLinkOutcome { Status = "failed", Reason = ex.Message };
            }
        }

        // ─── link_file — link ONE external file into the open model ────────
        // Dispatch-by-extension counterpart to batch_link_models's folder scan:
        // "link this DWG/IFC/RVT the drafter just received" for a single known
        // file, vs batch_link_models's "scan this folder for AR/ST/ACMV
        // consultant models". RVT reuses LinkOneRvt above (batch_link_models's
        // tested per-file link+verify path) rather than re-deriving it. CAD and
        // IFC are new here — see per-branch comments for the exact API verified
        // against the bundled RevitAPI reference (MetadataLoadContext dump,
        // 2023/2025/2027 target families all agree on the signatures used).
        public static Dictionary<string, object?> RunLinkFile(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;
            string? path = ArgsHelp.GetString(args, "path");
            string positioning = ArgsHelp.GetString(args, "positioning") ?? "origin";

            if (string.IsNullOrWhiteSpace(path))
                return Err("path is required — the absolute path to the CAD/IFC/Revit file to link");
            if (!File.Exists(path))
                return Err($"file not found: {path}");
            if (!string.Equals(positioning, "origin", StringComparison.OrdinalIgnoreCase))
                return Err($"unsupported positioning '{positioning}' — only 'origin' (origin-to-origin) is implemented");

            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".rvt" => LinkRvtFile(doc, path),
                ".dwg" or ".dxf" => LinkCadFile(uidoc, path),
                ".ifc" => LinkIfcFile(doc, path),
                _ => Err($"unsupported file type '{ext}' — link_file handles .rvt, .dwg, .dxf, .ifc"),
            };
        }

        private static Dictionary<string, object?> LinkRvtFile(Document doc, string path)
        {
            var fileName = Path.GetFileName(path);
            var existingTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            var outcome = LinkOneRvt(doc, path, pin: true, existingTypes);
            if (outcome.Status == "failed")
                return Err($"{fileName}: {outcome.Reason}");

            var warnings = new List<string>();
            if (outcome.Status == "reloaded")
                warnings.Add("already linked — reloaded the existing link instead of creating a new one");

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["linked_id"] = outcome.ElementId != null ? (long)outcome.ElementId.Value : null,
                ["kind"] = "rvt",
                ["file"] = fileName,
                ["warnings"] = warnings,
            };
        }

        // CAD (.dwg/.dxf) — Document.Link(string, DWGImportOptions, View, out
        // ElementId), verified via MetadataLoadContext against RevitAPI.dll for
        // net48/2023, net8.0-windows/2025 and net10.0-windows/2027 (identical
        // signature in all three). DXF has no separate DXFImportOptions type in
        // the API — DWGImportOptions covers both extensions, exactly as
        // DwgScratchCache.LinkInto already does for the read-only CAD path.
        // ThisViewOnly = false is what makes this a document-wide LINK (not a
        // view-specific import) — extract_cad_geometry's pipeline reads links,
        // not view-scoped imports, so this must stay false.
        private static Dictionary<string, object?> LinkCadFile(UIDocument uidoc, string path)
        {
            var doc = uidoc.Document;
            var fileName = Path.GetFileName(path);
            var view = PickHostView(uidoc);
            if (view == null)
                return Err("cannot link this CAD file: no plan view available to anchor the link (open a floor plan and try again)");

            var options = new DWGImportOptions
            {
                ThisViewOnly = false,
                Placement = ImportPlacement.Origin,
                ColorMode = ImportColorMode.Preserved,
            };

            int before = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance)).GetElementCount();

            using var tx = new Transaction(doc, $"BinaVibe: link CAD {fileName}");
            tx.Start();
            bool linked;
            ElementId id;
            try
            {
                linked = doc.Link(path, options, view, out id);
            }
            catch (Exception ex)
            {
                tx.RollBack();
                return Err($"{fileName}: {ex.Message}");
            }
            if (!linked || id == null || id == ElementId.InvalidElementId)
            {
                tx.RollBack();
                return Err($"Revit could not link {fileName} (corrupt, password-protected, or an unsupported CAD version)");
            }
            tx.Commit();

            int after = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance)).GetElementCount();
            var warnings = new List<string>();
            if (after <= before)
                warnings.Add("link reported success but no new CAD import instance was found by the verification collector");

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["linked_id"] = (long)id.Value,
                ["kind"] = "cad",
                ["file"] = fileName,
                ["warnings"] = warnings,
            };
        }

        private static View? PickHostView(UIDocument uidoc)
        {
            if (uidoc.ActiveView is ViewPlan active && !active.IsTemplate) return active;
            return new FilteredElementCollector(uidoc.Document)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate);
        }

        // IFC — RevitLinkType.CreateFromIFC(Document, string ifcFilePath, string
        // revitLinkedFilePath, bool recreateLink, RevitLinkOptions) is VERIFIED
        // present (MetadataLoadContext dump against RevitAPI.dll, identical
        // signature across net48/2023, net8.0-windows/2025, net10.0-windows/2027)
        // — this is a real, implemented path, not a faked one.
        //
        // CreateFromIFC alone does NOT create the sidecar Revit file: its own
        // XML doc says revitLinkedFilePath must be "an EXISTING Revit file
        // created via an import by reference operation" and lists
        // FileArgumentNotFoundException when no file exists at that path. So
        // "link an IFC" is a two-step dance, both steps verified present on the
        // same reference assembly:
        //   1. Application.OpenIFCDocument(path, IFCImportOptions{Action=Link})
        //      creates that intermediate document; Document.SaveAs writes it to
        //      the sidecar path (path + ".RVT") — we are the ones "creating" it,
        //      Revit's own API is what does the work.
        //   2. RevitLinkType.CreateFromIFC then links that sidecar into the host
        //      document, same Create+verify shape as the RVT path.
        // This compiles clean against the reference assembly but has NOT been
        // exercised against a live Revit process (no Windows/Revit host in this
        // dev environment) — any runtime failure surfaces as the real Revit
        // exception message, not a canned success.
        private static Dictionary<string, object?> LinkIfcFile(Document doc, string path)
        {
            var fileName = Path.GetFileName(path);
            var sidecar = path + ".RVT";
            bool sidecarExisted = File.Exists(sidecar);

            try
            {
                if (!sidecarExisted)
                {
                    var ifcOptions = new IFCImportOptions { Action = IFCImportAction.Link };
                    Document? ifcDoc = null;
                    try
                    {
                        ifcDoc = doc.Application.OpenIFCDocument(path, ifcOptions);
                        if (ifcDoc == null)
                            return Err($"Revit could not open the IFC file: {fileName}");
                        ifcDoc.SaveAs(sidecar);
                    }
                    finally
                    {
                        if (ifcDoc != null && ifcDoc.IsValidObject)
                        {
                            try { ifcDoc.Close(false); } catch { /* best effort */ }
                        }
                    }
                }

                int before = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).GetElementCount();

                using var tx = new Transaction(doc, $"BinaVibe: link IFC {fileName}");
                tx.Start();
                LinkLoadResult res;
                try
                {
                    // recreateLink=true when the sidecar was already on disk
                    // (possibly stale from an earlier attempt) so Revit
                    // refreshes it from the current IFC; false when we just
                    // wrote it fresh above — nothing to recreate.
                    res = RevitLinkType.CreateFromIFC(doc, path, sidecar, sidecarExisted, new RevitLinkOptions(false));
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return Err($"{fileName}: {ex.Message}");
                }
                if (res.LoadResult != LinkLoadResultType.LinkLoaded)
                {
                    tx.RollBack();
                    return Err($"{fileName}: {res.LoadResult}");
                }
                var inst = RevitLinkInstance.Create(doc, res.ElementId);
                if (inst != null) inst.Pinned = true;
                tx.Commit();

                int after = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).GetElementCount();
                var warnings = new List<string>();
                if (after <= before)
                    warnings.Add("link reported success but no new Revit link instance was found by the verification collector");

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["linked_id"] = (long)res.ElementId.Value,
                    ["kind"] = "ifc",
                    ["file"] = fileName,
                    ["warnings"] = warnings,
                };
            }
            catch (Exception ex)
            {
                return Err($"{fileName}: {ex.Message}");
            }
        }

        private static IEnumerable<string> SafeDirs(string folder)
        {
            try { return Directory.GetDirectories(folder); }
            catch { return Array.Empty<string>(); }
        }

        private static DateTime SafeWriteTime(string path)
        {
            try { return File.GetLastWriteTime(path); }
            catch { return DateTime.MinValue; }
        }
    }
}
