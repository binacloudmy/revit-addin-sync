using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// What this machine knows about a model file's place in BINA: the project,
    /// folder and model it was downloaded from or last synced into.
    ///
    /// The stamp inside the model only carries a Revit document GUID, and
    /// models uploaded through the web or promoted between areas have none, so
    /// a model downloaded from them is anonymous to the add-in. This record
    /// fills that gap without a server change: Download Model writes it, every
    /// successful Sync updates it, and the Sync dialog reads it to pre-select.
    /// </summary>
    public sealed class ModelRecord
    {
        public string Path { get; set; }
        public int ProjectId { get; set; }
        /// <summary>WIP folder only; null when the file came from Shared or Published.</summary>
        public int? FolderId { get; set; }
        public string DisciplineType { get; set; }
        public int? DesignId { get; set; }
        public string LineageId { get; set; }
        public string DocGuid { get; set; }
        public string Name { get; set; }
        /// <summary>download | sync — what wrote the record, for the dialog's wording.</summary>
        public string Source { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public static class ModelRecordStore
    {
        /// <summary>Old records drop off past this, newest kept.</summary>
        internal const int MaxRecords = 500;

        private static readonly object Gate = new object();

        public static string DefaultStorePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bina", "RevitSync", "models.json");

        /// <summary>The record for a model file, or null. Never throws.</summary>
        public static ModelRecord Find(string modelPath, string storePath = null)
        {
            string key = Key(modelPath);
            if (key == null) return null;

            lock (Gate)
            {
                return Load(storePath ?? DefaultStorePath)
                    .FirstOrDefault(r => string.Equals(Key(r.Path), key, StringComparison.Ordinal));
            }
        }

        /// <summary>Adds or replaces the record for its path. Never throws.</summary>
        public static void Save(ModelRecord record, string storePath = null)
        {
            if (record == null || Key(record.Path) == null || record.ProjectId <= 0) return;

            string path = storePath ?? DefaultStorePath;
            lock (Gate)
            {
                try
                {
                    string key = Key(record.Path);
                    record.UpdatedUtc = DateTime.UtcNow;

                    var records = Load(path)
                        .Where(r => !string.Equals(Key(r.Path), key, StringComparison.Ordinal))
                        .ToList();
                    records.Add(record);

                    var kept = records
                        .OrderByDescending(r => r.UpdatedUtc)
                        .Take(MaxRecords)
                        .ToList();

                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                    // Written aside and moved into place, so a crash mid-write
                    // leaves the old file rather than half a JSON document.
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(kept, Formatting.Indented));
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);
                }
                catch
                {
                    // A record we could not write only costs a pre-selection.
                }
            }
        }

        private static List<ModelRecord> Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<ModelRecord>();
                return JsonConvert.DeserializeObject<List<ModelRecord>>(File.ReadAllText(path))
                       ?? new List<ModelRecord>();
            }
            catch
            {
                // Corrupt or unreadable: start over rather than fail the dialog.
                return new List<ModelRecord>();
            }
        }

        /// <summary>Full path, case-folded: Windows paths compare case-insensitively.</summary>
        internal static string Key(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                return System.IO.Path.GetFullPath(path).Trim().ToLowerInvariant();
            }
            catch
            {
                return path.Trim().ToLowerInvariant();
            }
        }
    }
}
