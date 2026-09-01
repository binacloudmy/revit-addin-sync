using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    public class AIRequest
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        [JsonProperty("context")]
        public ModelContext Context { get; set; }

        [JsonProperty("userId")]
        public int? UserId { get; set; }

        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        [JsonProperty("templateId")]
        public string TemplateId { get; set; }

        /// <summary>Screenshots pasted with the prompt (base64 PNG). Omitted from
        /// the JSON when null so un-upgraded backends see an unchanged body.</summary>
        [JsonProperty("images", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Images { get; set; }

        /// <summary>P2 slash command: the backend command id the drafter picked
        /// from the slash menu (authoritative — the backend injects that
        /// definition's instructions + tool allowlist, no prompt parsing).
        /// Omitted when null so a plain NL turn is byte-identical to before.</summary>
        [JsonProperty("command_id", NullValueHandling = NullValueHandling.Ignore)]
        public string CommandId { get; set; }

        /// <summary>P2 slash command args (from param chips), keyed by the
        /// definition's arg names. Omitted when null.</summary>
        [JsonProperty("command_args", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> CommandArgs { get; set; }
    }

    /// <summary>The env header attached to /tool/generate — the Claude Code
    /// "env block" analog, static identity only. Scene state (levels, views,
    /// selection, scene digest) is NOT pushed: the agent pulls it on demand
    /// via READ tools (get_scene_overview, list_*, query_geometry). The
    /// backend RevitModelContext keeps its legacy scene fields as optional,
    /// so old addin builds that still push them stay compatible.</summary>
    public class ModelContext
    {
        [JsonProperty("projectName")]
        public string ProjectName { get; set; }

        [JsonProperty("revitVersion")]
        public string RevitVersion { get; set; }

        /// <summary>Addin assembly version. Its presence marks a lean-context
        /// client whose ToolRegistry knows get_scene_overview — the backend
        /// can gate newer-tool advertisement on it.</summary>
        [JsonProperty("addin_version", NullValueHandling = NullValueHandling.Ignore)]
        public string AddinVersion { get; set; }

        /// <summary>Capability negotiation (bina-ai spec §8.2, R1 Task 15) —
        /// additive: all three omitted when null so an old backend sees the
        /// legacy four-key header unchanged. Populated from the GENERATED
        /// InstalledToolManifest when VibeFlags.ManifestHandshake is on; the
        /// backend intersects installed_tools with its own manifest and the
        /// tenant's entitlement and refuses anything outside that set before
        /// a frame is serialised.</summary>
        [JsonProperty("protocol_version", NullValueHandling = NullValueHandling.Ignore)]
        public int? ProtocolVersion { get; set; }

        [JsonProperty("manifest_version", NullValueHandling = NullValueHandling.Ignore)]
        public string ManifestVersion { get; set; }

        [JsonProperty("installed_tools", NullValueHandling = NullValueHandling.Ignore)]
        public string[] InstalledTools { get; set; }

        /// <summary>
        /// Identifies the backend snapshot namespace for this project.
        /// Must match the {project} segment used by DocumentChangedIndexer
        /// when POSTing to /revit-copilot/snapshot/{tenant}/{project} so the backend
        /// can read the mirror for this specific model.
        /// Serialised as "project_id" to match the backend RevitModelContext field.
        /// </summary>
        [JsonProperty("project_id")]
        public string ProjectId { get; set; }
    }
}
