// Lean-context wire contract (VibeFlags.LeanContext, pull-based scene sight).
//
// Locks two things:
//  1. VibeFlags json: LeanContext defaults true (pull-based is the shipping
//     path) and {"LeanContext": false} in vibe.json is the rollback lever.
//  2. The lean ModelContext body serializes to ONLY the env-header keys
//     ({project_id, projectName, revitVersion, addin_version}) and OMITS every
//     scene key entirely (not null-studded) — the sparse shape the staging
//     backend was probed to accept. A scene key reappearing here means the
//     NullValueHandling.Ignore contract broke and old backends may see a
//     different body than the one verified.

using System.Collections.Generic;
using BinaVibe.Policy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Models;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LeanContextTests
    {
        // ─── VibeFlags ──────────────────────────────────────────────────

        [Fact]
        public void LeanContext_DefaultsTrue_WhenAbsentFromJson()
        {
            var flags = System.Text.Json.JsonSerializer.Deserialize<VibeFlags>("{}");
            Assert.NotNull(flags);
            Assert.True(flags.LeanContext);
        }

        [Fact]
        public void LeanContext_RollbackParsesFalse()
        {
            // The per-machine rollback documented on the flag:
            // {"LeanContext": false} in vibe.json restores the legacy push.
            var flags = System.Text.Json.JsonSerializer.Deserialize<VibeFlags>(
                "{\"LeanContext\": false}");
            Assert.NotNull(flags);
            Assert.False(flags.LeanContext);
        }

        [Fact]
        public void LeanContext_FreshDefaults_True()
        {
            Assert.True(new VibeFlags().LeanContext);
        }

        // ─── lean ModelContext serialization ────────────────────────────

        private static ModelContext LeanContext() => new ModelContext
        {
            ProjectId = "42",
            ProjectName = "Rumah Teres",
            RevitVersion = "2027",
            AddinVersion = "1.2.3.0",
        };

        [Fact]
        public void LeanBody_ContainsExactlyEnvHeaderKeys()
        {
            var json = JsonConvert.SerializeObject(LeanContext());
            var obj = JObject.Parse(json);

            var keys = new List<string>();
            foreach (var p in obj.Properties()) keys.Add(p.Name);
            keys.Sort(System.StringComparer.Ordinal);

            Assert.Equal(
                new[] { "addin_version", "projectName", "project_id", "revitVersion" },
                keys);
        }

        [Fact]
        public void LeanBody_OmitsSceneKeysEntirely()
        {
            var json = JsonConvert.SerializeObject(LeanContext());
            var obj = JObject.Parse(json);

            // Scene keys must be ABSENT, not null — the sparse body the
            // staging backend accepted has no trace of them.
            foreach (var sceneKey in new[]
            {
                "levels", "categories", "phases", "selectedElementIds",
                "sceneDigest", "views", "activeViewName", "activeViewType",
            })
                Assert.False(obj.ContainsKey(sceneKey),
                    $"scene key '{sceneKey}' leaked into the lean body");
        }

        [Fact]
        public void FullContext_StillSerializesSceneKeys()
        {
            // The flag-off path must be byte-compatible with the legacy body:
            // populated scene fields still serialize under their old names.
            var full = new ModelContext
            {
                ProjectName = "P",
                RevitVersion = "2027",
                Levels = new List<string> { "Aras 01" },
                Categories = new List<string> { "Walls" },
                Phases = new List<string> { "New Construction" },
                SelectedElementIds = new List<int> { 1001 },
                ActiveViewName = "Aras 01",
                ActiveViewType = "FloorPlan",
                Views = new List<ViewInfo>
                {
                    new ViewInfo { Id = 7, Name = "Aras 01", ViewType = "FloorPlan", OwnerView = "Aras 01" },
                },
                SceneDigest = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["id"] = 1001 },
                },
            };

            var obj = JObject.Parse(JsonConvert.SerializeObject(full));
            foreach (var key in new[]
            {
                "levels", "categories", "phases", "selectedElementIds",
                "sceneDigest", "views", "activeViewName", "activeViewType",
            })
                Assert.True(obj.ContainsKey(key), $"legacy key '{key}' missing from full body");
        }

        [Fact]
        public void AIRequest_LeanContext_NestsUnderContextKey()
        {
            var req = new AIRequest
            {
                Prompt = "hi",
                Context = LeanContext(),
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
