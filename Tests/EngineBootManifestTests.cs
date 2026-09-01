// The boot manifest is the ONLY thing standing between the logon launcher and a
// re-implementation of UrlResolution/NewestEngineLauncher/ProviderKeyEnvs in
// PowerShell. These tests pin the two properties that make that safe: it carries
// the DERIVED values verbatim, and it never carries a credential.

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class EngineBootManifestTests
    {
        private static readonly DateTime When = new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Utc);

        private static Dictionary<string, string> SpawnEnv() => new Dictionary<string, string>
        {
            ["BINA_ENGINE"] = "1",
            ["BINA_ENGINE_SECRET"] = "s3cr3t-engine-secret",
            ["BINA_ENGINE_PORT"] = "48810",
            ["BINA_GATEWAY_URL"] = "https://bina-ai-prod.azurewebsites.net",
            ["BINA_ENGINE_TOKEN"] = "device-token-abc123",
            ["LANGFUSE_BASE_URL"] = "https://bina-ai-prod.azurewebsites.net/gateway/langfuse",
            ["LANGFUSE_PUBLIC_KEY"] = "device-token-abc123",
            ["LANGFUSE_SECRET_KEY"] = "engine",
        };

        private static EngineBootManifest Build(IDictionary<string, string> env = null) =>
            EngineBootManifest.Build(
                env ?? SpawnEnv(),
                port: 48810,
                launcher: @"C:\Users\a b\AppData\Local\Bina\RevitSync\engine\1.0.1\run-engine.cmd",
                workingDir: @"C:\Users\a b\AppData\Local\Bina\RevitSync\engine\1.0.1",
                addinVersion: "0.0.57",
                stripEnv: new[] { "DEEPSEEK_API_KEY", "OPENAI_API_KEY" },
                utcNow: When);

        [Fact]
        public void Secrets_are_referenced_by_config_field_never_by_value()
        {
            var m = Build();

            Assert.DoesNotContain("BINA_ENGINE_SECRET", m.Env.Keys);
            Assert.DoesNotContain("BINA_ENGINE_TOKEN", m.Env.Keys);
            Assert.DoesNotContain("LANGFUSE_PUBLIC_KEY", m.Env.Keys);

            Assert.Equal("EngineSecret", m.SecretEnv["BINA_ENGINE_SECRET"]);
            Assert.Equal("DeviceToken", m.SecretEnv["BINA_ENGINE_TOKEN"]);
            // The gateway's Langfuse proxy authenticates with the device token as
            // the "public key" — it is a credential, not a public identifier.
            Assert.Equal("DeviceToken", m.SecretEnv["LANGFUSE_PUBLIC_KEY"]);
        }

        [Fact]
        public void Serialized_manifest_contains_no_secret_value()
        {
            var json = Build().ToJson();

            Assert.DoesNotContain("s3cr3t-engine-secret", json);
            Assert.DoesNotContain("device-token-abc123", json);
        }

        [Fact]
        public void Derived_values_survive_verbatim()
        {
            var m = Build();

            // The four things the launcher must NOT re-derive.
            Assert.Equal(48810, m.Port);
            Assert.EndsWith(@"engine\1.0.1\run-engine.cmd", m.Launcher);
            Assert.Equal("https://bina-ai-prod.azurewebsites.net", m.Env["BINA_GATEWAY_URL"]);
            Assert.Equal(new[] { "DEEPSEEK_API_KEY", "OPENAI_API_KEY" }, m.StripEnv);

            // Non-secret env passes through untouched.
            Assert.Equal("1", m.Env["BINA_ENGINE"]);
            Assert.Equal("engine", m.Env["LANGFUSE_SECRET_KEY"]);
            Assert.Equal("https://bina-ai-prod.azurewebsites.net/gateway/langfuse", m.Env["LANGFUSE_BASE_URL"]);
        }

        [Fact]
        public void Blank_secret_is_not_advertised_to_the_launcher()
        {
            // EngineManager only sets the gateway/token vars when configured; an
            // empty value must not become a "read DeviceToken from config" order,
            // or the launcher reports a missing credential that was never wanted.
            var env = SpawnEnv();
            env["BINA_ENGINE_TOKEN"] = "";

            var m = Build(env);

            Assert.False(m.SecretEnv.ContainsKey("BINA_ENGINE_TOKEN"));
            Assert.DoesNotContain("BINA_ENGINE_TOKEN", m.Env.Keys);
        }

        [Fact]
        public void Json_uses_the_snake_case_keys_the_launcher_reads()
        {
            var o = JObject.Parse(Build().ToJson());

            // engine-boot.ps1 reads these names; renaming a property without
            // bumping the schema silently disables auto-start at logon.
            Assert.Equal(EngineBootManifest.CurrentSchema, (int)o["schema"]);
            Assert.Equal(48810, (int)o["port"]);
            Assert.NotNull(o["launcher"]);
            Assert.NotNull(o["working_dir"]);
            Assert.NotNull(o["env"]);
            Assert.NotNull(o["secret_env"]);
            Assert.NotNull(o["strip_env"]);
            Assert.Equal("0.0.57", (string)o["addin_version"]);
            Assert.Equal(When, o["written_at_utc"].Value<DateTime>().ToUniversalTime());
        }

        [Fact]
        public void Secret_sources_name_real_BinaConfig_properties()
        {
            // These strings are read out of config.json BY NAME at logon. A typo,
            // or a rename on the BinaConfig side, makes the launcher read null and
            // skip the boot silently forever — so the mapping is pinned here.
            // (BinaConfig itself can't be linked into this project: it P/Invokes
            // the Windows Credential Manager. BinaConfig.cs carries the matching
            // "keep in sync" note.)
            Assert.Equal("EngineSecret", EngineBootManifest.SecretEnvSources["BINA_ENGINE_SECRET"]);
            Assert.Equal("DeviceToken", EngineBootManifest.SecretEnvSources["BINA_ENGINE_TOKEN"]);
            Assert.Equal("DeviceToken", EngineBootManifest.SecretEnvSources["LANGFUSE_PUBLIC_KEY"]);
            Assert.Equal(3, EngineBootManifest.SecretEnvSources.Count);
        }
    }
}
