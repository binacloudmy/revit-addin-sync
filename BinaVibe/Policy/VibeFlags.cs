// BinaVibe.Policy — local feature flags + tenant config.
//
// Per PRD §10.8 (per-tenant policy) and §10.12 (sovereignty toggle).
// Read from `%APPDATA%\RevitWebAppSync\vibe.json` if present, otherwise
// fall back to defaults.

using System;
using System.IO;
using System.Text.Json;

namespace BinaVibe.Policy
{
    public sealed class VibeFlags
    {
        public bool UseVibeV2 { get; init; } = false;
        public string TenantId { get; init; } = "default";
        public string? UserId { get; init; }
        public bool Sovereign { get; init; } = false;
        public bool ReadOnly { get; init; } = false;
        public int ApprovalTimeoutSeconds { get; init; } = 300;

        /// <summary>Pull-based scene context: when true the copilot sends only a
        /// tiny static env header ({project_id, projectName, revitVersion,
        /// addin_version}) on /tool/generate and the agent gathers scene sight
        /// on demand via READ tools (get_scene_overview, list_*, query_geometry)
        /// instead of receiving a pushed ModelContext snapshot every turn.
        /// Default ON (bina-ai prompt/tooling side landed). Rollback per
        /// machine: {"LeanContext": false} in vibe.json.</summary>
        public bool LeanContext { get; init; } = true;

        public static VibeFlags Load()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitWebAppSync",
                    "vibe.json");
                if (!File.Exists(path)) return new VibeFlags();
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<VibeFlags>(json) ?? new VibeFlags();
            }
            catch
            {
                return new VibeFlags();
            }
        }
    }
}
