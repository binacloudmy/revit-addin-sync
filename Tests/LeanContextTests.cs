// Lean-context wire contract (pull-based scene sight).
//
// The addin sends ONLY the static env header {project_id, projectName,
// revitVersion, addin_version} on /tool/generate — scene state is pulled by
// the agent via READ tools (get_scene_overview, list_*, query_geometry).
// These tests pin that wire shape; the server half is pinned by bina-ai's
// tests/test_lean_context.py (sparse body parses, addin_version aliases,
// legacy snapshots stay compatible).

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Models;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LeanContextTests
    {
        private static ModelContext EnvHeader() => new ModelContext
        {
            ProjectId = "42",
            ProjectName = "Rumah Teres",
            RevitVersion = "2027",
            AddinVersion = "1.2.3.0",
        };

        [Fact]
        public void EnvHeader_CarriesCapabilityHandshake_WhenPopulated()
        {
            // Spec §8.2: additive keys — protocol_version, manifest_version,
            // installed_tools. The backend intersects these with its own
            // manifest; a client that omits them is treated as legacy.
            var ctx = EnvHeader();
            ctx.ProtocolVersion = 2;
            ctx.ManifestVersion = "abcdef012345";
            ctx.InstalledTools = new[] { "list_levels", "create_wall" };
            var obj = JObject.Parse(JsonConvert.SerializeObject(ctx));

            Assert.Equal(2, (int)obj["protocol_version"]!);
            Assert.Equal("abcdef012345", (string)obj["manifest_version"]!);
            Assert.Equal(new[] { "list_levels", "create_wall" },
                         obj["installed_tools"]!.ToObject<string[]>());
        }

        [Fact]
        public void EnvHeader_OmitsHandshakeKeys_WhenNull()
        {
            // Byte-identical to the pre-negotiation header when not populated,
            // so an old backend sees exactly the four legacy keys.
            var obj = JObject.Parse(JsonConvert.SerializeObject(EnvHeader()));
            Assert.False(obj.ContainsKey("protocol_version"));
            Assert.False(obj.ContainsKey("manifest_version"));
            Assert.False(obj.ContainsKey("installed_tools"));
        }

        [Fact]
        public void EnvHeader_ContainsExactlyTheFourKeys()
        {
            var obj = JObject.Parse(JsonConvert.SerializeObject(EnvHeader()));

            var keys = new System.Collections.Generic.List<string>();
            foreach (var p in obj.Properties()) keys.Add(p.Name);
            keys.Sort(System.StringComparer.Ordinal);

            Assert.Equal(
                new[] { "addin_version", "projectName", "project_id", "revitVersion" },
                keys);
        }

        [Fact]
        public void EnvHeader_NeverCarriesSceneKeys()
        {
            // Regression guard: a scene key reappearing here means someone
            // reintroduced pushed context — scene sight must stay pull-based.
            var obj = JObject.Parse(JsonConvert.SerializeObject(EnvHeader()));
            foreach (var sceneKey in new[]
            {
                "levels", "categories", "phases", "selectedElementIds",
                "sceneDigest", "views", "activeViewName", "activeViewType",
            })
                Assert.False(obj.ContainsKey(sceneKey),
                    $"scene key '{sceneKey}' leaked into the env header");
        }

        [Fact]
        public void AddinVersion_OmittedWhenNull()
        {
            var obj = JObject.Parse(JsonConvert.SerializeObject(new ModelContext
            {
                ProjectId = "1",
                ProjectName = "P",
                RevitVersion = "2027",
            }));
            Assert.False(obj.ContainsKey("addin_version"));
        }

        [Fact]
        public void AIRequest_EnvHeader_NestsUnderContextKey()
        {
            var req = new AIRequest
            {
                Prompt = "hi",
                Context = EnvHeader(),
                UserId = 1,
                SessionId = "s-1",
            };
            var obj = JObject.Parse(JsonConvert.SerializeObject(req));

            Assert.True(obj.ContainsKey("context"));
            Assert.Equal("42", (string)obj["context"]!["project_id"]!);
            // Pasted-screenshot field stays omitted when null (existing contract).
            Assert.False(obj.ContainsKey("images"));
        }
    }
}
