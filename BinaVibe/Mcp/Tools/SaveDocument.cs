// save_document_as — guarded Save As (bina-ai R2 Task 24).
//
//   save_document_as {file_name, directory?, overwrite?, dry_run, confirm_token?}
//
// dry_run: the plan only — destination, whether it exists, the current
// document's state (path, title, modified, workshared) and every refusal —
// plus a confirm_token bound to the exact target path. Apply: refuses unless
// confirm_token matches the same plan recomputed NOW (so nothing can change
// between the preview the drafter approved and the write), then
// Document.SaveAs with OverwriteExistingFile = overwrite, and verifies the
// file exists and the document's PathName is the target. Non-workshared only
// in this release; a workshared document is refused, never detached.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using BinaVibe.Saving;

namespace BinaVibe.Mcp.Tools
{
    internal static class SaveDocument
    {
        private static SaveAsFacts FactsFor(Document doc, string dir, string file)
        {
            string? central = null;
            bool workshared = false;
            try
            {
                workshared = doc.IsWorkshared;
                if (workshared)
                {
                    var mp = doc.GetWorksharingCentralModelPath();
                    central = mp != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) : null;
                }
            }
            catch { }
            bool dirExists = false, writable = false, exists = false;
            try
            {
                dirExists = Directory.Exists(dir);
                if (dirExists)
                {
                    var probe = Path.Combine(dir, ".bina-write-probe-" + Guid.NewGuid().ToString("N"));
                    try { File.WriteAllText(probe, ""); File.Delete(probe); writable = true; } catch { writable = false; }
                    exists = File.Exists(Path.Combine(dir, file));
                }
            }
            catch { }
            return new SaveAsFacts
            {
                CurrentPath = doc.PathName ?? "",
                Title = doc.Title ?? "",
                IsModified = doc.IsModified,
                IsWorkshared = workshared,
                CentralPath = central,
                TargetExists = exists,
                DirectoryExists = dirExists,
                Writable = writable,
            };
        }

        public static Dictionary<string, object?> Run(Document doc, JsonElement args)
        {
            var fileNameRaw = ArgsHelp.GetString(args, "file_name") ?? throw new ArgumentException("missing file_name");
            var file = SaveAsPlan.NormalizeFileName(fileNameRaw);
            var dir = ArgsHelp.GetString(args, "directory");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var overwrite = ArgsHelp.GetBool(args, "overwrite") ?? false;
            var dryRun = ArgsHelp.GetBool(args, "dry_run") ?? false;

            var facts = FactsFor(doc, dir!, file);
            var plan = SaveAsPlan.Build(facts, dir!, file, overwrite);
            if (dryRun) return plan.ToPreview();

            // Confirmation immediately before write: the token must match the
            // plan recomputed now, and the plan must still be saveable.
            var token = ArgsHelp.GetString(args, "confirm_token") ?? "";
            if (!string.Equals(token, plan.ConfirmToken, StringComparison.Ordinal))
                return new() { ["ok"] = false, ["code"] = "token_mismatch",
                               ["error"] = "confirm_token does not match the previewed destination — run the preview again and confirm" };
            if (!plan.WouldSave)
                return new() { ["ok"] = false, ["code"] = plan.Refusals[0].Code, ["error"] = plan.Refusals[0].Note,
                               ["refusals"] = plan.ToPreview()["refusals"] };

            var previous = doc.PathName ?? "";
            var opts = new SaveAsOptions { OverwriteExistingFile = overwrite, MaximumBackups = 3, Compact = false };
            doc.SaveAs(plan.TargetPath, opts);

            bool fileExists = false; long size = 0; bool pathMatches = false;
            try
            {
                fileExists = File.Exists(plan.TargetPath);
                if (fileExists) size = new FileInfo(plan.TargetPath).Length;
                pathMatches = string.Equals(Path.GetFullPath(doc.PathName ?? ""), Path.GetFullPath(plan.TargetPath), StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["saved_path"] = plan.TargetPath,
                ["previous_path"] = previous,
                ["title"] = doc.Title,
                ["overwrote"] = plan.Overwrites,
                ["confirm_token_matched"] = true,
                ["verified"] = new Dictionary<string, object?>
                {
                    ["file_exists"] = fileExists, ["document_path_matches"] = pathMatches, ["size_bytes"] = size,
                },
                ["undo"] = "not applicable: Save As is a file operation; the previous file is untouched",
                ["headline"] = fileExists && pathMatches ? $"saved as {plan.TargetPath}" : "save reported but could not be verified",
            };
            try
            {
                var ledger = BinaVibe.DocState.DocumentRevisionTracker.LedgerFor(doc);
                result["document_fingerprint"] = ledger.Fingerprint;
                result["document_revision"] = ledger.Revision;
            }
            catch { }
            return result;
        }
    }
}
