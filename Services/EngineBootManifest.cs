// EngineBootManifest — the handoff contract between the add-in and the
// logon-time boot launcher (installer\engine-boot.ps1).
//
// WHY THIS EXISTS. The boot launcher has to start the engine with the SAME
// port, gateway URL, bundle and environment the add-in would have used. Every
// one of those is a DERIVED value, not a raw config.json field:
//
//   port          cfg.EngineHostPort (config default 48810, but settable)
//   gateway URL   cfg.ResolvedGatewayUrl — UrlResolution.ResolveGateway
//                 REWRITES a persisted *.azurewebsites.net URL to the build's
//                 embedded default unless AllowBackendOverride is set. That is
//                 the mechanism that moves an already-configured fleet across a
//                 backend cutover with nothing but a new build, so a launcher
//                 reading the raw GatewayUrl would point the boot-started engine
//                 at the PREVIOUS backend on exactly the machines the rule
//                 exists for.
//   bundle        NewestEngineLauncher's newest-semver pick, AND the
//                 min_addin_version gate (TooOldForEngine) that can refuse it.
//   env           the Langfuse gateway-proxy trio + the provider-key strip list.
//
// Re-implementing those four rules in PowerShell guarantees drift: the next
// person to touch ResolveGateway or ProviderKeyEnvs has no reason to know a
// .ps1 also encodes them. So instead the add-in RECORDS what it actually used,
// and the launcher REPLAYS it verbatim. One source of truth, in C#, unit-tested.
//
// SECRETS ARE NOT RECORDED. The engine secret and the gateway device token are
// referenced by the NAME of the config.json field that holds them (`secret_env`)
// and read live by the launcher — so this file never becomes a second on-disk
// copy of a credential, and a rotated token is picked up at the next logon
// instead of pinning the value that was current when the manifest was written.
//
// Consequence to know: the manifest only exists once the add-in has spawned the
// engine at least once. A brand-new install therefore does not auto-start at
// logon until Revit has been opened once — which is also when the device token
// first exists, so nothing useful is lost.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    public sealed class EngineBootManifest
    {
        /// <summary>Bumped whenever the shape changes incompatibly. The launcher
        /// refuses a manifest it does not recognise rather than guessing.</summary>
        public const int CurrentSchema = 1;

        /// <summary>Env keys whose VALUE is a credential: recorded by name only,
        /// mapped to the config.json field the launcher must read them from.
        /// LANGFUSE_PUBLIC_KEY is the device token by design — /gateway/langfuse
        /// authenticates the machine with it (see EngineManager's spawn path).</summary>
        public static readonly IReadOnlyDictionary<string, string> SecretEnvSources =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BINA_ENGINE_SECRET"] = "EngineSecret",
                ["BINA_ENGINE_TOKEN"] = "DeviceToken",
                ["LANGFUSE_PUBLIC_KEY"] = "DeviceToken",
            };

        [JsonProperty("schema")] public int Schema { get; set; } = CurrentSchema;
        [JsonProperty("addin_version")] public string AddinVersion { get; set; }
        [JsonProperty("written_at_utc")] public string WrittenAtUtc { get; set; }

        /// <summary>Loopback port the add-in health-checks. The launcher MUST
        /// use this one: starting on a different port leaves the add-in probing
        /// an empty port and spawning a second engine.</summary>
        [JsonProperty("port")] public int Port { get; set; }

        /// <summary>Full path to the launcher the add-in ran — the bundle that
        /// already passed the newest-semver pick AND the min_addin_version gate.</summary>
        [JsonProperty("launcher")] public string Launcher { get; set; }
        [JsonProperty("working_dir")] public string WorkingDir { get; set; }

        /// <summary>Non-secret environment, verbatim.</summary>
        [JsonProperty("env")] public Dictionary<string, string> Env { get; set; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>env key -> config.json field to read its value from.</summary>
        [JsonProperty("secret_env")] public Dictionary<string, string> SecretEnv { get; set; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Provider keys the launcher must remove from the child
        /// environment — poison-pill parity with EngineManager.ProviderKeyEnvs
        /// and bina-ai's app/engine/config.py. Empty when not in gateway mode.</summary>
        [JsonProperty("strip_env")] public string[] StripEnv { get; set; } = Array.Empty<string>();

        /// <summary>Build a manifest from the environment the add-in is about to
        /// hand the engine. Secret-valued keys are moved out of <see cref="Env"/>
        /// into <see cref="SecretEnv"/> as field-name references — the caller can
        /// pass its live spawn environment without pre-scrubbing it.</summary>
        public static EngineBootManifest Build(
            IDictionary<string, string> spawnEnv,
            int port,
            string launcher,
            string workingDir,
            string addinVersion,
            IEnumerable<string> stripEnv,
            DateTime utcNow)
        {
            if (spawnEnv == null) throw new ArgumentNullException(nameof(spawnEnv));

            var m = new EngineBootManifest
            {
                AddinVersion = addinVersion,
                WrittenAtUtc = utcNow.ToString("o"),
                Port = port,
                Launcher = launcher,
                WorkingDir = workingDir,
                StripEnv = (stripEnv ?? Array.Empty<string>()).ToArray(),
            };

            foreach (var kv in spawnEnv)
            {
                if (kv.Key == null) continue;
                if (SecretEnvSources.TryGetValue(kv.Key, out var field))
                {
                    // Reference, never the value. A key present with an EMPTY
                    // value was deliberately not set by the add-in (the gateway
                    // block is conditional) — don't ask the launcher for it.
                    if (!string.IsNullOrEmpty(kv.Value)) m.SecretEnv[kv.Key] = field;
                    continue;
                }
                m.Env[kv.Key] = kv.Value ?? "";
            }

            return m;
        }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
    }
}
