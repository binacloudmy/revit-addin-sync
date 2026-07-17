using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Pure wire model for /telemetry/events (fleet health heartbeats).
    /// Dependency-free (no Revit SDK, no IO) so the Tests project can
    /// compile-link it, same as AiUrl. PII contract: payload carries exception
    /// TYPE NAMES only — never messages, prompts, or paths.
    /// </summary>
    internal sealed class TelemetryEvent
    {
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("stage")] public string Stage { get; set; }
        [JsonProperty("machine_id")] public string MachineId { get; set; }
        [JsonProperty("addin_version")] public string AddinVersion { get; set; }
        [JsonProperty("revit_year")] public string RevitYear { get; set; }
        [JsonProperty("engine_version")] public string EngineVersion { get; set; }
        [JsonProperty("payload")] public object Payload { get; set; }
        [JsonProperty("occurred_at")] public string OccurredAt { get; set; }

        internal static TelemetryEvent Create(
            string kind, string stage, string machineId, string addinVersion,
            string revitYear, string engineVersion, object payload, DateTime utcNow)
        {
            return new TelemetryEvent
            {
                Kind = kind ?? string.Empty,
                Stage = stage ?? string.Empty,
                MachineId = machineId ?? string.Empty,
                AddinVersion = addinVersion ?? string.Empty,
                RevitYear = revitYear ?? string.Empty,
                EngineVersion = engineVersion ?? string.Empty,
                Payload = payload ?? new Dictionary<string, string>(),
                // InvariantCulture: some locales swap the ':' time separator,
                // which would corrupt the wire format.
                OccurredAt = utcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                                             CultureInfo.InvariantCulture),
            };
        }
    }

    internal static class TelemetryIdentity
    {
        /// <summary>Pseudonymous fleet identity: 16-hex SHA256 prefix of
        /// machine+user. Stable per install, not reversible to a name.</summary>
        internal static string MachineId(string machineName, string userName)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(
                    Encoding.UTF8.GetBytes((machineName ?? "") + "|" + (userName ?? "")));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }

    internal static class TelemetryBatch
    {
        internal static string ToJson(IReadOnlyList<TelemetryEvent> events) =>
            JsonConvert.SerializeObject(new { events });
    }
}
