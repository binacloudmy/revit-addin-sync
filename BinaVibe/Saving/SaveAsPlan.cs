// BinaVibe.Saving — guarded Save As planner, Revit-free (bina-ai R2 Task 24).
//
// Every decision that does not need Revit lives here: target path, name
// validation, refusals (workshared in the first release, existing target
// without explicit overwrite, missing/unwritable directory, same path as the
// current document, invalid name), and the confirm token bound to the exact
// destination that the apply must echo — "confirmation immediately before
// write" on the wire. Nothing here touches the file system.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BinaVibe.Saving
{
    public sealed class SaveAsFacts
    {
        public string CurrentPath { get; init; } = "";
        public string Title { get; init; } = "";
        public bool IsModified { get; init; }
        public bool IsWorkshared { get; init; }
        public string? CentralPath { get; init; }
        public bool TargetExists { get; init; }
        public bool DirectoryExists { get; init; }
        public bool Writable { get; init; }
    }

    public sealed class Refusal
    {
        public string Code { get; init; } = "";
        public string Note { get; init; } = "";
    }

    public sealed class SaveAsPlan
    {
        public string Directory { get; }
        public string FileName { get; }
        public string TargetPath { get; }
        public bool Overwrite { get; }
        public bool Overwrites => Overwrite && Facts.TargetExists;
        public SaveAsFacts Facts { get; }
        public IReadOnlyList<Refusal> Refusals { get; }
        public bool WouldSave => Refusals.Count == 0;
        public string ConfirmToken => TokenFor(TargetPath);

        private SaveAsPlan(string dir, string file, string target, bool overwrite, SaveAsFacts facts, List<Refusal> refusals)
        { Directory = dir; FileName = file; TargetPath = target; Overwrite = overwrite; Facts = facts; Refusals = refusals; }

        public static string TokenFor(string targetPath)
        {
            using var sha = SHA256.Create();
            var hex = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(targetPath.Trim().ToLowerInvariant())))
                .Replace("-", "").ToLowerInvariant();
            return hex.Substring(0, 12);
        }

        public static string NormalizeFileName(string fileName)
        {
            var f = (fileName ?? "").Trim();
            if (!f.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) f += ".rvt";
            return f;
        }

        private static bool ValidName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\')) return false;
            var bad = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '?', '*', '<', '>', '|', '"' };
            return !fileName.Any(bad.Contains);
        }

        public static SaveAsPlan Build(SaveAsFacts facts, string directory, string fileName, bool overwrite)
        {
            var file = NormalizeFileName(fileName);
            var dir = (directory ?? "").Trim();
            var refusals = new List<Refusal>();
            var valid = ValidName(file);
            if (!valid)
                refusals.Add(new Refusal { Code = "invalid_name", Note = $"'{fileName}' is not a valid Revit file name" });
            var target = valid ? Path.Combine(dir, file) : file;

            if (facts.IsWorkshared)
                refusals.Add(new Refusal { Code = "workshared_not_supported", Note = "workshared models are not supported by guarded Save As yet (first release is non-workshared only); use Revit's own Save As / Detach" });
            if (valid && string.Equals(Path.GetFullPath(target), SafeFull(facts.CurrentPath), StringComparison.OrdinalIgnoreCase))
                refusals.Add(new Refusal { Code = "same_as_current", Note = "that is the current file; use Save, not Save As" });
            if (!facts.DirectoryExists)
                refusals.Add(new Refusal { Code = "directory_missing", Note = $"folder does not exist: {dir}" });
            else if (!facts.Writable)
                refusals.Add(new Refusal { Code = "directory_not_writable", Note = $"folder is not writable: {dir}" });
            if (facts.TargetExists && !overwrite)
                refusals.Add(new Refusal { Code = "target_exists", Note = $"{file} already exists in {dir}; say overwrite to replace it" });

            return new SaveAsPlan(dir, file, target, overwrite, facts, refusals);
        }

        private static string SafeFull(string p)
        {
            try { return string.IsNullOrEmpty(p) ? "" : Path.GetFullPath(p); } catch { return p ?? ""; }
        }

        public Dictionary<string, object?> ToPreview() => new()
        {
            ["ok"] = true,
            ["dry_run"] = true,
            ["current"] = new Dictionary<string, object?>
            {
                ["path"] = Facts.CurrentPath, ["title"] = Facts.Title,
                ["is_modified"] = Facts.IsModified, ["is_workshared"] = Facts.IsWorkshared, ["central_path"] = Facts.CentralPath,
            },
            ["target"] = new Dictionary<string, object?>
            {
                ["directory"] = Directory, ["file_name"] = FileName, ["path"] = TargetPath,
                ["exists"] = Facts.TargetExists, ["directory_exists"] = Facts.DirectoryExists, ["writable"] = Facts.Writable,
            },
            ["overwrite"] = Overwrite,
            ["refusals"] = Refusals.Select(r => (object)new Dictionary<string, object?> { ["code"] = r.Code, ["note"] = r.Note }).ToList(),
            ["would_save"] = WouldSave,
            ["confirm_token"] = ConfirmToken,
            ["headline"] = WouldSave
                ? $"would save as {TargetPath}" + (Overwrites ? " (overwriting the existing file)" : "") + " — nothing written yet"
                : $"cannot save: {string.Join("; ", Refusals.Select(r => r.Code))}",
        };
    }
}
