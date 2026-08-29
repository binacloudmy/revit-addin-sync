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

        /// <summary>Capability negotiation (bina-ai spec §8.2/§8.7). When on,
        /// every /tool/generate env header carries protocol_version,
        /// manifest_version and installed_tools from the GENERATED
        /// InstalledToolManifest, so the backend intersects what it plans with
        /// what this build dispatches. Default OFF: the header stays the
        /// legacy four keys and an enforcing backend treats us as a legacy,
        /// query-only client. Enable in the same batch as the backend flag.</summary>
        public bool ManifestHandshake { get; init; } = false;

        /// <summary>Document revision tracking (bina-ai spec §8.4). When on,
        /// every tool result is stamped with document_fingerprint +
        /// document_revision, mutation frames carrying expected_revision are
        /// checked BEFORE a transaction opens (stale_document on mismatch),
        /// and changes_since serves a bounded delta. Default OFF. Enable in
        /// the same batch as the backend's REVIT_AI_REQUIRE_REVISION.</summary>
        public bool RevisionTracking { get; init; } = false;

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
