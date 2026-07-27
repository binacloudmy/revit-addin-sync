using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// One family in the BINA cloud library, as returned by
    /// GET /family-library/list on bina-ai.
    /// </summary>
    public class FamilyLibraryItem
    {
        [JsonProperty("library_id")]
        public string LibraryId { get; set; }

        [JsonProperty("family_name")]
        public string FamilyName { get; set; }

        /// <summary>Raw JKR category, e.g. "Lighting Fixtures".</summary>
        [JsonProperty("category")]
        public string Category { get; set; }

        /// <summary>The filter bucket this rolls up into, e.g. "Electrical".</summary>
        [JsonProperty("chip")]
        public string Chip { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("file_type")]
        public string FileType { get; set; }

        [JsonProperty("file_size")]
        public long? FileSize { get; set; }

        /// <summary>
        /// Revit version the family was authored in. Anything above the running
        /// Revit cannot be loaded, so the grid greys those out.
        /// </summary>
        [JsonProperty("revit_version")]
        public int? RevitVersion { get; set; }

        /// <summary>
        /// False for the ~27% of the catalog that are 2D-symbol families Revit
        /// never rendered a preview for. Saves the grid a round trip that would
        /// only 404.
        /// </summary>
        [JsonProperty("has_thumbnail")]
        public bool HasThumbnail { get; set; }

        [JsonProperty("type_names")]
        public List<string> TypeNames { get; set; } = new List<string>();

        /// <summary>Which families to pull out when file_type is "rvt".</summary>
        [JsonProperty("source_names")]
        public List<string> SourceNames { get; set; } = new List<string>();

        /// <summary>Human-readable size for the card subtitle ("2.4 MB").</summary>
        [JsonIgnore]
        public string SizeLabel =>
            FileSize.HasValue && FileSize.Value > 0
                ? (FileSize.Value >= 1024 * 1024
                    ? $"{FileSize.Value / 1024.0 / 1024.0:0.#} MB"
                    : $"{FileSize.Value / 1024.0:0} KB")
                : "";

        /// <summary>"Doors · 2.4 MB" — the line under the family name.</summary>
        [JsonIgnore]
        public string SubtitleLabel =>
            string.IsNullOrEmpty(SizeLabel) ? Chip : $"{Chip} · {SizeLabel}";

        [JsonIgnore]
        public string VersionLabel =>
            RevitVersion.HasValue ? $"Revit {RevitVersion.Value}" : "";
    }

    /// <summary>One page of GET /family-library/list.</summary>
    public class FamilyLibraryPage
    {
        [JsonProperty("items")]
        public List<FamilyLibraryItem> Items { get; set; } = new List<FamilyLibraryItem>();

        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("page")]
        public int Page { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }

        [JsonProperty("pages")]
        public int Pages { get; set; }
    }

    /// <summary>One filter chip and how many families sit behind it.</summary>
    public class FamilyLibraryCategory
    {
        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }
    }

    public class FamilyLibraryCategories
    {
        [JsonProperty("categories")]
        public List<FamilyLibraryCategory> Categories { get; set; }
            = new List<FamilyLibraryCategory>();
    }

    /// <summary>
    /// GET /family-library/{id}/download-url — a short-lived presigned link.
    /// The fields mirror what Mutators.LoadFamily expects, so the manual load
    /// runs the exact same path the copilot's load_family tool does.
    /// </summary>
    public class FamilyDownloadTicket
    {
        [JsonProperty("library_id")]
        public string LibraryId { get; set; }

        [JsonProperty("family_name")]
        public string FamilyName { get; set; }

        [JsonProperty("file_type")]
        public string FileType { get; set; }

        [JsonProperty("file_size")]
        public long? FileSize { get; set; }

        [JsonProperty("source_names")]
        public List<string> SourceNames { get; set; } = new List<string>();

        [JsonProperty("download_url")]
        public string DownloadUrl { get; set; }

        [JsonProperty("expires_in_seconds")]
        public int ExpiresInSeconds { get; set; }
    }
}
