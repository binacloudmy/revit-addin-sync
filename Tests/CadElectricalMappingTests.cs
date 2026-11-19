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
            ConduitTypeMatches = text => RunTypeInfo.Matches(ConduitTypes, text),
            ConduitSizesMm = name => ConduitTypes.Single(t => t.Name == name).SizesMm,
        };

        private static readonly RunTypeInfo[] ConduitTypes =
        {
            new RunTypeInfo { Name = "Conduit with Fittings : PVC", TypeName = "PVC", WithFittings = true, SizesMm = { 16, 20, 25, 32 } },
            new RunTypeInfo { Name = "Conduit with Fittings : Steel", TypeName = "Steel", WithFittings = true, SizesMm = { 20, 25 } },
            new RunTypeInfo { Name = "Conduit without Fittings : PVC", TypeName = "PVC", WithFittings = false },
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
        [InlineData(@"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit""}]}", "missing_evidence", "layers[0].evidence")]
        [InlineData(@"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""conduit_type"": ""Bamboo"", ""evidence"": ""e""}]}", "conduit_type_not_found", "layers[0].conduit_type")]
        [InlineData(@"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""conduit_type"": ""pvc"", ""evidence"": ""e""}]}", "conduit_type_ambiguous", "layers[0].conduit_type")]
        [InlineData(@"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""diameter_mm"": ""20"", ""evidence"": ""e""}]}", "bad_diameter", "layers[0].diameter_mm")]
        [InlineData(@"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""diameter_mm"": 0, ""evidence"": ""e""}]}", "bad_diameter", "layers[0].diameter_mm")]
        [InlineData(@"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""diameter_mm"": 250, ""evidence"": ""e""}]}", "bad_diameter", "layers[0].diameter_mm")]
        [InlineData(@"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""run_height_mm"": 12000, ""evidence"": ""e""}]}", "bad_run_height", "layers[0].run_height_mm")]
        [InlineData(@"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""run_height_mm"": -1500, ""evidence"": ""e""}]}", "bad_run_height", "layers[0].run_height_mm")]
        [InlineData(@"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""conduit_type"": ""Steel"", ""diameter_mm"": 32, ""evidence"": ""e""}]}", "diameter_not_in_standard", "layers[0].diameter_mm")]
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
        public void Conduit_row_keeps_type_diameter_and_height()
        {
            (CadElectricalMapping mapping, List<MappingIssue> issues) = ParseAndValidate(
                @"{""layers"": [{""layer"": ""e-conduit"", ""role"": ""conduit"", ""conduit_type"": ""conduit with fittings : pvc"",
                    ""diameter_mm"": 20, ""run_height_mm"": 2700, ""evidence"": ""legend: '20mm PVC CONDUIT'""}]}");

            Assert.Empty(issues);
            LayerRow row = Assert.Single(mapping.Layers);
            Assert.Equal("E-CONDUIT", row.Layer);
            Assert.Equal("conduit", row.Role);
            Assert.Equal("Conduit with Fittings : PVC", row.ConduitType);
            Assert.Equal(20, row.DiameterMm);
            Assert.Equal(2700, row.RunHeightMm);
            Assert.Equal("legend: '20mm PVC CONDUIT'", row.Evidence);
            Assert.Empty(mapping.Notes);
            Assert.Empty(mapping.BackgroundLayers());
            Assert.Null(CadElectricalMapping.UnplannedReason(row, null));
        }

        [Fact]
        public void A_size_off_the_standard_lists_the_types_sizes()
        {
            (_, List<MappingIssue> issues) = ParseAndValidate(
                @"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""conduit_type"": ""Conduit with Fittings : PVC"",
                    ""diameter_mm"": 21, ""evidence"": ""e""}]}");

            MappingIssue issue = Assert.Single(issues);
            Assert.Equal("diameter_not_in_standard", issue.Code);
            Assert.Equal(new[] { "16", "20", "25", "32" }, issue.Candidates);
        }

        [Fact]
        public void Conduit_row_without_a_type_is_unplanned_not_rejected()
        {
            (CadElectricalMapping mapping, List<MappingIssue> issues) = ParseAndValidate(
                @"{""layers"": [{""layer"": ""E-CONDUIT"", ""role"": ""conduit"", ""diameter_mm"": 20, ""evidence"": ""e""}]}");

            Assert.Empty(issues);
            Assert.Equal("no_conduit_type", CadElectricalMapping.UnplannedReason(mapping.Layers[0], 2700));

            var noDiameter = new LayerRow { Layer = "E-CONDUIT", Role = "conduit", ConduitType = "Conduit with Fittings : PVC" };
            Assert.Equal("no_diameter", CadElectricalMapping.UnplannedReason(noDiameter, 2700));
            Assert.Null(CadElectricalMapping.UnplannedReason(new LayerRow { Layer = "A-WALL", Role = "background" }, null));
        }

        [Fact]
        public void Layer_height_wins_over_the_argument()
        {
            var row = new LayerRow { Role = "conduit", ConduitType = "Conduit with Fittings : PVC", DiameterMm = 20, RunHeightMm = 3000 };
            Assert.Equal(3000, CadElectricalMapping.EffectiveRunHeight(row, 2700));
            row.RunHeightMm = null;
            Assert.Equal(2700, CadElectricalMapping.EffectiveRunHeight(row, 2700));
        }

        [Fact]
        public void No_height_anywhere_is_no_run_height()
        {
            var row = new LayerRow { Role = "conduit", ConduitType = "Conduit with Fittings : PVC", DiameterMm = 20 };
            Assert.Equal("no_run_height", CadElectricalMapping.UnplannedReason(row, null));
            Assert.Null(CadElectricalMapping.EffectiveRunHeight(row, null));
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
