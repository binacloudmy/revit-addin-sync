#nullable enable
// CadElectricalMapping: the AI's table of what each block class and layer is, parsed and
// checked against the drawing and the model. Invented names only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class CadElectricalMappingTests
    {
        private const string SpecExample = @"{
          ""symbols"": [
            {""block"": ""SOCKET"", ""role"": ""socket"", ""family_type"": ""Socket Outlet : 13A Single"",
             ""mount_height_mm"": 300, ""host"": ""wall"", ""evidence"": ""legend: '13A SWITCHED SOCKET OUTLET'""},
            {""block"": ""LIGHT-A"", ""role"": ""light"", ""family_type"": null, ""mount_height_mm"": null,
             ""host"": ""ceiling"", ""evidence"": ""legend row""},
            {""block"": ""anon:1a2b3c4d"", ""role"": ""unknown"", ""evidence"": ""no legend row, no text""}
          ],
          ""layers"": [ {""layer"": ""A-WALL"", ""role"": ""background""}, {""layer"": ""E-POWER"", ""role"": ""ignore""} ],
          ""plans"": [ {""cluster"": 0, ""level"": ""Level 1"", ""evidence"": ""title text""} ]
        }";

        private static readonly string[] ModelTypes =
        {
            "Socket Outlet : 13A Single", "Socket Outlet : 13A Twin", "Switch : 1 Gang",
        };

        private static MappingFacts Facts() => new MappingFacts
        {
            BlockKeys = new HashSet<string>(StringComparer.Ordinal) { "SOCKET", "LIGHT-A", "anon:1a2b3c4d", "SWITCH-1G" },
            LayerNames = new HashSet<string>(StringComparer.Ordinal) { "A-WALL", "E-POWER", "E-CONDUIT", "0" },
            PlanCount = 2,
            Levels = new HashSet<string>(StringComparer.Ordinal) { "Level 1", "Level 2" },
            FamilyTypeMatches = text =>
            {
                List<string> exact = ModelTypes.Where(t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase)).ToList();
                return exact.Count > 0
                    ? exact
                    : ModelTypes.Where(t => t.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            },
        };

        private static JsonElement Json(string text)
        {
            using (JsonDocument doc = JsonDocument.Parse(text)) return doc.RootElement.Clone();
        }

        private static (CadElectricalMapping Mapping, List<MappingIssue> Issues) ParseAndValidate(string json)
        {
            var issues = new List<MappingIssue>();
            CadElectricalMapping mapping = CadElectricalMapping.Parse(Json(json), issues);
            mapping.Validate(Facts(), issues);
            return (mapping, issues);
        }

        [Fact]
        public void Parses_the_spec_example()
        {
            (CadElectricalMapping mapping, List<MappingIssue> issues) = ParseAndValidate(SpecExample);

            Assert.Empty(issues);
            Assert.Equal(3, mapping.Symbols.Count);
            SymbolRow socket = mapping.Symbols[0];
            Assert.Equal("SOCKET", socket.Block);
            Assert.Equal("socket", socket.Role);
            Assert.Equal("Socket Outlet : 13A Single", socket.FamilyType);
            Assert.Equal(300, socket.MountHeightMm);
            Assert.Equal("wall", socket.Host);
            Assert.False(socket.HostDefaulted);
            Assert.Null(CadElectricalMapping.UnplannedReason(socket));

            SymbolRow light = mapping.Symbols[1];
            Assert.Null(light.FamilyType);
            Assert.Null(light.MountHeightMm);
            Assert.Equal("no_family_type", CadElectricalMapping.UnplannedReason(light));
            Assert.Equal("role_unknown", CadElectricalMapping.UnplannedReason(mapping.Symbols[2]));

            Assert.Equal(new[] { "A-WALL" }, mapping.BackgroundLayers());
            Assert.Equal("ignore", mapping.Layers[1].Role);
            PlanRow plan = Assert.Single(mapping.Plans);
            Assert.Equal(0, plan.Cluster);
            Assert.Equal("Level 1", plan.Level);
            Assert.Equal("title text", plan.Evidence);
        }

        [Fact]
        public void A_json_string_holding_the_object_is_accepted()
        {
            string asText = JsonSerializer.Serialize(SpecExample);

            (CadElectricalMapping mapping, List<MappingIssue> issues) = ParseAndValidate(asText);

            Assert.Empty(issues);
            Assert.Equal(3, mapping.Symbols.Count);
        }

        [Theory]
        [InlineData(@"[1, 2]", "not_an_object", "")]
        [InlineData(@"""not json""", "not_an_object", "")]
        [InlineData(@"{""symbols"": {""block"": ""SOCKET""}}", "not_an_object", "symbols")]
        [InlineData(@"{""symbols"": [{""block"": ""SOCKET"", ""role"": ""toaster"", ""evidence"": ""e""}]}", "unknown_role", "symbols[0].role")]
        [InlineData(@"{""layers"": [{""layer"": ""A-WALL"", ""role"": ""walls""}]}", "unknown_role", "layers[0].role")]
        [InlineData(@"{""symbols"": [{""block"": ""SOCKET"", ""role"": ""socket"", ""host"": ""floor"", ""evidence"": ""e""}]}", "unknown_host", "symbols[0].host")]
        [InlineData(@"{""symbols"": [{""block"": ""SOCKET"", ""role"": ""socket"", ""evidence"": ""e""}, {""block"": ""socket"", ""role"": ""socket"", ""evidence"": ""e""}]}", "duplicate_block", "symbols[1].block")]
        [InlineData(@"{""layers"": [{""layer"": ""A-WALL"", ""role"": ""background""}, {""layer"": ""A-WALL"", ""role"": ""ignore""}]}", "duplicate_layer", "layers[1].layer")]
        [InlineData(@"{""symbols"": [{""block"": ""NO-SUCH"", ""role"": ""socket"", ""evidence"": ""e""}]}", "block_not_in_drawing", "symbols[0].block")]
        [InlineData(@"{""layers"": [{""layer"": ""E-NONE"", ""role"": ""ignore""}]}", "layer_not_in_drawing", "layers[0].layer")]
        [InlineData(@"{""symbols"": [{""block"": ""SOCKET"", ""role"": ""socket"", ""evidence"": "" ""}]}", "missing_evidence", "symbols[0].evidence")]
        [InlineData(@"{""plans"": [{""cluster"": 0, ""level"": ""Level 1""}]}", "missing_evidence", "plans[0].evidence")]
        [InlineData(@"{""symbols"": [{""block"": ""SOCKET"", ""role"": ""socket"", ""mount_height_mm"": ""300"", ""evidence"": ""e""}]}", "bad_mount_height", "symbols[0].mount_height_mm")]
        [InlineData(@"{""symbols"": [{""block"": ""SOCKET"", ""role"": ""socket"", ""mount_height_mm"": -1, ""evidence"": ""e""}]}", "bad_mount_height", "symbols[0].mount_height_mm")]
        [InlineData(@"{""symbols"": [{""block"": ""SOCKET"", ""role"": ""socket"", ""mount_height_mm"": 4500, ""evidence"": ""e""}]}", "bad_mount_height", "symbols[0].mount_height_mm")]
        [InlineData(@"{""plans"": [{""cluster"": 2, ""level"": ""Level 1"", ""evidence"": ""e""}]}", "cluster_out_of_range", "plans[0].cluster")]
        [InlineData(@"{""plans"": [{""cluster"": 0.5, ""evidence"": ""e""}]}", "cluster_out_of_range", "plans[0].cluster")]
        [InlineData(@"{""plans"": [{""cluster"": 0, ""level"": ""Level 9"", ""evidence"": ""e""}]}", "level_not_found", "plans[0].level")]
        [InlineData(@"{""symbols"": [{""block"": ""SOCKET"", ""role"": ""socket"", ""family_type"": ""Plug : Round"", ""evidence"": ""e""}]}", "family_type_not_found", "symbols[0].family_type")]
        [InlineData(@"{""symbols"": [{""block"": ""SOCKET"", ""role"": ""socket"", ""family_type"": ""Socket Outlet"", ""evidence"": ""e""}]}", "family_type_ambiguous", "symbols[0].family_type")]
        public void Each_error_code_is_reported_at_its_path(string json, string code, string path)
        {
            (_, List<MappingIssue> issues) = ParseAndValidate(json);

            MappingIssue issue = Assert.Single(issues);
            Assert.Equal(code, issue.Code);
            Assert.Equal(path, issue.Path);
            Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        }

        [Fact]
        public void Ambiguous_and_missing_names_list_their_candidates()
        {
            (_, List<MappingIssue> issues) = ParseAndValidate(@"{
              ""symbols"": [{""block"": ""SOCKET"", ""role"": ""socket"", ""family_type"": ""Socket Outlet"", ""evidence"": ""e""},
                            {""block"": ""SWITCH"", ""role"": ""switch"", ""evidence"": ""e""}],
              ""plans"": [{""cluster"": 0, ""level"": ""Roof"", ""evidence"": ""e""}]
            }");

            Assert.Equal(new[] { "Socket Outlet : 13A Single", "Socket Outlet : 13A Twin" },
                         issues.Single(i => i.Code == "family_type_ambiguous").Candidates);
            Assert.Equal(new[] { "SWITCH-1G" }, issues.Single(i => i.Code == "block_not_in_drawing").Candidates);
            Assert.Equal(new[] { "Level 1", "Level 2" }, issues.Single(i => i.Code == "level_not_found").Candidates);
        }

        [Fact]
        public void A_unique_case_insensitive_match_takes_the_drawings_spelling()
        {
            (CadElectricalMapping mapping, List<MappingIssue> issues) = ParseAndValidate(@"{
              ""symbols"": [{""block"": ""socket"", ""role"": ""Socket"", ""family_type"": ""socket outlet : 13a twin"", ""mount_height_mm"": 300, ""evidence"": ""e""}],
              ""layers"": [{""layer"": ""a-wall"", ""role"": ""background""}],
              ""plans"": [{""cluster"": 1, ""level"": ""level 2"", ""evidence"": ""e""}]
            }");

            Assert.Empty(issues);
            Assert.Equal("SOCKET", mapping.Symbols[0].Block);
            Assert.Equal("socket", mapping.Symbols[0].Role);
            Assert.Equal("Socket Outlet : 13A Twin", mapping.Symbols[0].FamilyType);
            Assert.Equal("A-WALL", mapping.Layers[0].Layer);
            Assert.Equal("Level 2", mapping.Plans[0].Level);
        }

        [Fact]
        public void Null_host_defaults_by_role_and_says_so()
        {
            (CadElectricalMapping mapping, List<MappingIssue> issues) = ParseAndValidate(@"{""symbols"": [
              {""block"": ""SOCKET"", ""role"": ""socket"", ""evidence"": ""e""},
              {""block"": ""LIGHT-A"", ""role"": ""light"", ""host"": null, ""evidence"": ""e""},
              {""block"": ""SWITCH-1G"", ""role"": ""ignore"", ""evidence"": ""e""}
            ]}");

            Assert.Empty(issues);
            Assert.Equal("wall", mapping.Symbols[0].Host);
            Assert.True(mapping.Symbols[0].HostDefaulted);
            Assert.Equal("ceiling", mapping.Symbols[1].Host);
            Assert.True(mapping.Symbols[1].HostDefaulted);
            Assert.Null(mapping.Symbols[2].Host);
            Assert.False(mapping.Symbols[2].HostDefaulted);
            Assert.Contains("host_defaulted: SOCKET -> wall", mapping.Notes);
            Assert.Contains("host_defaulted: LIGHT-A -> ceiling", mapping.Notes);
            Assert.Equal(2, mapping.Notes.Count);
        }

        [Fact]
        public void A_wall_device_with_no_height_plans_nothing_but_does_not_reject()
        {
            (CadElectricalMapping mapping, List<MappingIssue> issues) = ParseAndValidate(@"{""symbols"": [
              {""block"": ""SOCKET"", ""role"": ""socket"", ""family_type"": ""Socket Outlet : 13A Single"", ""evidence"": ""e""},
              {""block"": ""LIGHT-A"", ""role"": ""light"", ""family_type"": ""Switch : 1 Gang"", ""evidence"": ""e""}
            ]}");

            Assert.Empty(issues);
            Assert.Equal("no_mount_height", CadElectricalMapping.UnplannedReason(mapping.Symbols[0]));
            Assert.Null(CadElectricalMapping.UnplannedReason(mapping.Symbols[1]));
        }

        [Fact]
        public void All_issues_come_back_in_one_pass()
        {
            (_, List<MappingIssue> issues) = ParseAndValidate(@"{
              ""symbols"": [{""block"": ""NO-SUCH"", ""role"": ""toaster"", ""host"": ""floor"", ""mount_height_mm"": 9000}],
              ""layers"": [{""layer"": ""E-NONE"", ""role"": ""walls""}],
              ""plans"": [{""cluster"": 7, ""level"": ""Level 9""}]
            }");

            Assert.Equal(
                new[]
                {
                    "bad_mount_height", "block_not_in_drawing", "cluster_out_of_range", "layer_not_in_drawing",
                    "level_not_found", "missing_evidence", "unknown_host", "unknown_role",
                },
                issues.Select(i => i.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal));
            Assert.Equal(2, issues.Count(i => i.Code == "missing_evidence"));
            Assert.Equal(2, issues.Count(i => i.Code == "unknown_role"));
        }

        [Fact]
        public void Conduit_layer_is_accepted_and_reported_as_deferred()
        {
            (CadElectricalMapping mapping, List<MappingIssue> issues) = ParseAndValidate(
                @"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit""}]}");

            Assert.Empty(issues);
            Assert.Equal("conduit", mapping.Layers[0].Role);
            Assert.Contains("conduit_deferred: layer E-CONDUIT (phase 2)", mapping.Notes);
            Assert.Empty(mapping.BackgroundLayers());
        }

        [Fact]
        public void Only_ignore_and_unknown_are_not_placeable()
        {
            Assert.Equal(new[] { "ignore", "unknown" },
                         CadElectricalMapping.Roles.Where(role => !CadElectricalMapping.IsPlaceable(role)));
            Assert.False(CadElectricalMapping.IsPlaceable("toaster"));
        }
    }
}
