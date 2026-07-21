using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
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
            int InstanceCount() => new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).GetElementCount();

            int created = 0, reloaded = 0, failed = 0, skipped = 0;
            var details = new List<string>();
            string? firstFailReason = null;

            // ── 3. link each candidate (Create outside tx, place inside) ─────
            foreach (var (path, disc) in candidates)
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
                        reloaded++;
                        details.Add($"{fileName} [{disc}] -> RELOADED (already linked)");
                        continue;
                    }

                    int before = InstanceCount();
                    // The tool executor runs us WITHOUT an ambient transaction, and
                    // in that context RevitLinkType.Create throws "Modifying is
                    // forbidden because the document has no open transaction"
                    // (round 52 — the tool surfaced its own bug via the failure
                    // reason). Both Create and the instance placement go inside
                    // ONE transaction.
                    LinkLoadResult res;
                    using (var tx = new Transaction(doc, $"Link {fileName}"))
                    {
                        tx.Start();
                        res = RevitLinkType.Create(doc, mp, new RevitLinkOptions(false));
                        if (res.LoadResult != LinkLoadResultType.LinkLoaded)
                        {
                            tx.RollBack();
                            failed++;
                            var reason = res.LoadResult.ToString();
                            firstFailReason ??= $"{fileName}: {reason}";
                            details.Add($"{fileName} [{disc}] -> FAILED ({reason})");
                            continue;
                        }
                        var inst = RevitLinkInstance.Create(doc, res.ElementId);
                        if (pin && inst != null) inst.Pinned = true;
                        tx.Commit();
                    }
                    int after = InstanceCount();
                    if (after > before)
                    {
                        created++;
                        details.Add($"{fileName} [{disc}] -> CREATED (origin-to-origin{(pin ? ", pinned" : "")})");
                    }
                    else
                    {
                        failed++;
                        firstFailReason ??= $"{fileName}: created type but no instance appeared";
                        details.Add($"{fileName} [{disc}] -> FAILED (no instance after create)");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    firstFailReason ??= $"{fileName}: {ex.Message}";
                    details.Add($"{fileName} [{disc}] -> FAILED ({ex.Message})");
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
