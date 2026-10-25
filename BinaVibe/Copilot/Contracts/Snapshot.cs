#nullable enable
// Copilot context snapshot (spec §7.9, plan §2.4; owned by W0, contracts).
// Revit-free mirror of contracts/copilot/snapshot.schema.json. W2's
// SnapshotService keeps one current from Revit events and sends it with every
// turn and results request. Doc and View are null when no document is open.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BinaVibe.Copilot.Contracts
{
    public static class SnapshotDocKinds
    {
        public const string Project = "project";
        public const string Family = "family";

        public static IReadOnlyList<string> All { get; } = new[] { Project, Family };
    }

    public static class SnapshotDocModes
    {
        public const string Central = "central";
        public const string Local = "local";
        public const string Detached = "detached";
        public const string Standalone = "standalone";
        public const string ReadOnly = "read_only";

        public static IReadOnlyList<string> All { get; } = new[] { Central, Local, Detached, Standalone, ReadOnly };
    }

    public static class SnapshotLimits
    {
        public const int MaxSelectionIds = 50;
        /// <summary>Rooms are listed only when the model has at most this many.</summary>
        public const int MaxRooms = 150;
    }

    public sealed class SnapshotModel
    {
        [JsonPropertyName("doc")] public SnapshotDoc? Doc { get; set; }
        [JsonPropertyName("view")] public SnapshotView? View { get; set; }
        [JsonPropertyName("selection")] public SnapshotSelection Selection { get; set; } = new();
        [JsonPropertyName("worksharing")] public SnapshotWorksharing Worksharing { get; set; } = new();
        [JsonPropertyName("links")] public List<SnapshotLink> Links { get; set; } = new();
        [JsonPropertyName("warnings_count")] public int WarningsCount { get; set; }
        /// <summary>Null when the model has more than <see cref="SnapshotLimits.MaxRooms"/> rooms.</summary>
        [JsonPropertyName("rooms")] public List<SnapshotRoom>? Rooms { get; set; }
        /// <summary>Increases on every DocumentChanged the add-in saw.</summary>
        [JsonPropertyName("change_seq")] public long ChangeSeq { get; set; }
        /// <summary>True when the last refresh failed.</summary>
        [JsonPropertyName("stale")] public bool Stale { get; set; }
    }

    public sealed class SnapshotDoc
    {
        /// <summary>One of <see cref="SnapshotDocKinds"/>.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        /// <summary>Lowercase hex SHA-256 of Document.PathName (UTF-8); null for an unsaved document.</summary>
        [JsonPropertyName("path_hash")] public string? PathHash { get; set; }
        [JsonPropertyName("is_workshared")] public bool IsWorkshared { get; set; }
        /// <summary>One of <see cref="SnapshotDocModes"/>.</summary>
        [JsonPropertyName("mode")] public string Mode { get; set; } = "";
        [JsonPropertyName("units")] public SnapshotUnits Units { get; set; } = new();
        /// <summary>Active view phase name.</summary>
        [JsonPropertyName("phase")] public string? Phase { get; set; }
        /// <summary>Null for the main model.</summary>
        [JsonPropertyName("active_design_option")] public string? ActiveDesignOption { get; set; }
        /// <summary>Document.GetDocumentVersion(doc).VersionGUID.</summary>
        [JsonPropertyName("version_guid")] public string VersionGuid { get; set; } = "";
        /// <summary>Random per opened document.</summary>
        [JsonPropertyName("session_nonce")] public string SessionNonce { get; set; } = "";
        /// <summary>e.g. "2025".</summary>
        [JsonPropertyName("revit_version")] public string RevitVersion { get; set; } = "";
    }

    public sealed class SnapshotUnits
    {
        /// <summary>Display length unit symbol, e.g. "mm".</summary>
        [JsonPropertyName("length")] public string Length { get; set; } = "";
    }

    public sealed class SnapshotView
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        /// <summary>Revit ViewType name, e.g. "FloorPlan".</summary>
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("level")] public string? Level { get; set; }
        [JsonPropertyName("scale")] public int? Scale { get; set; }
        [JsonPropertyName("is_template")] public bool IsTemplate { get; set; }
    }

    public sealed class SnapshotSelection
    {
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("by_category")] public Dictionary<string, int> ByCategory { get; set; } = new();
        /// <summary>The first <see cref="SnapshotLimits.MaxSelectionIds"/> selected ids.</summary>
        [JsonPropertyName("ids")] public List<long> Ids { get; set; } = new();
    }

    public sealed class SnapshotWorksharing
    {
        /// <summary>Null when the document is not workshared.</summary>
        [JsonPropertyName("active_workset")] public string? ActiveWorkset { get; set; }
    }

    public sealed class SnapshotLink
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("loaded")] public bool Loaded { get; set; }
    }

    public sealed class SnapshotRoom
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("number")] public string Number { get; set; } = "";
        [JsonPropertyName("level")] public string Level { get; set; } = "";
    }
}
