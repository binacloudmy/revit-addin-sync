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
    }

    /// <summary>Context attached to /tool/generate. Two shapes share this DTO:
    /// the legacy full scene snapshot (BuildContext) and the lean env header
    /// (BuildEnvContext, VibeFlags.LeanContext) — {project_id, projectName,
    /// revitVersion, addin_version} only, scene fields null. Scene fields carry
    /// NullValueHandling.Ignore so the lean body omits them entirely instead of
    /// sending null-studded keys (verified: staging backend accepts the sparse
    /// body, no 422).</summary>
    public class ModelContext
    {
        [JsonProperty("projectName")]
        public string ProjectName { get; set; }

        [JsonProperty("levels", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Levels { get; set; }

        [JsonProperty("categories", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Categories { get; set; }

        [JsonProperty("activeViewName", NullValueHandling = NullValueHandling.Ignore)]
        public string ActiveViewName { get; set; }

        [JsonProperty("activeViewType", NullValueHandling = NullValueHandling.Ignore)]
        public string ActiveViewType { get; set; }

        [JsonProperty("selectedElementIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<int> SelectedElementIds { get; set; }

        /// <summary>Phase 2 scene digest: placement facts for the working set
        /// so the agent SEES where things are (xyz/facing/room/host) without a
        /// query_geometry round-trip. Each entry {id, xyz, facing, room, hostId}.
        /// Bounded in BuildContext. Serialised as "sceneDigest" to match the
        /// backend RevitModelContext.scene_digest field.</summary>
        [JsonProperty("sceneDigest", NullValueHandling = NullValueHandling.Ignore)]
        public List<Dictionary<string, object>> SceneDigest { get; set; }

        /// <summary>Real view list (id + name + type) so the agent resolves
        /// "open Aras 01" to the exact view instead of guessing. Bounded by
        /// BuildContext to avoid dumping thousands of views.</summary>
        [JsonProperty("views", NullValueHandling = NullValueHandling.Ignore)]
        public List<ViewInfo> Views { get; set; }

        [JsonProperty("phases", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Phases { get; set; }

        [JsonProperty("revitVersion")]
        public string RevitVersion { get; set; }

        /// <summary>Addin assembly version, sent only by lean-context builds.
        /// The backend gates advertisement of newer tools (get_scene_overview)
        /// on its presence so an old addin is never asked to run a tool its
        /// ToolRegistry doesn't know.</summary>
        [JsonProperty("addin_version", NullValueHandling = NullValueHandling.Ignore)]
        public string AddinVersion { get; set; }

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

    public class ViewInfo
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("viewType")] public string ViewType { get; set; }
        /// <summary>The level a plan view belongs to — disambiguates same-named views.</summary>
        [JsonProperty("ownerView")] public string OwnerView { get; set; }
    }
}
