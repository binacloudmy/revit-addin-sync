using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-471: what the Revit reader leaves out of the built model (linked content, non-primary
    /// design options, demolished elements) and how a missing parameter differs from an empty one.
    /// The Revit side only turns an element into these facts; the decisions are here.
    /// </summary>
    public class SpecExtractionRulesTests
    {
        // ── linked models ──

        [Fact]
        public void Element_that_lives_only_in_a_link_is_not_counted()
        {
            Assert.False(SpecExtractionRules.CountLinkedContent);
            Assert.Equal(SpecExtractionRules.ExcludedLinked,
                SpecExtractionRules.ExcludeReason(true, SpecDesignOptionKind.None, SpecPhaseState.New));
        }

        // ── design options ──

        [Theory]
        [InlineData(SpecDesignOptionKind.None, null)]
        [InlineData(SpecDesignOptionKind.Primary, null)]
        [InlineData(SpecDesignOptionKind.NonPrimary, SpecExtractionRules.ExcludedDesignOption)]
        public void Only_non_primary_design_option_elements_are_left_out(SpecDesignOptionKind kind, string reason)
        {
            Assert.Equal(reason, SpecExtractionRules.ExcludeReason(false, kind, SpecPhaseState.New));
        }

        // ── phases ──

        [Theory]
        [InlineData(SpecPhaseState.Unknown, null)]
        [InlineData(SpecPhaseState.Existing, null)]
        [InlineData(SpecPhaseState.New, null)]
        [InlineData(SpecPhaseState.Temporary, null)]
        [InlineData(SpecPhaseState.Demolished, SpecExtractionRules.ExcludedDemolished)]
        [InlineData(SpecPhaseState.Past, SpecExtractionRules.ExcludedDemolished)]
        [InlineData(SpecPhaseState.Future, SpecExtractionRules.ExcludedDemolished)]
        public void Demolished_elements_are_left_out_of_the_built_model(SpecPhaseState phase, string reason)
        {
            Assert.Equal(reason, SpecExtractionRules.ExcludeReason(false, SpecDesignOptionKind.None, phase));
        }

        [Theory]
        [InlineData("Existing", SpecPhaseState.Existing)]
        [InlineData("New", SpecPhaseState.New)]
        [InlineData("Temporary", SpecPhaseState.Temporary)]
        [InlineData("Demolished", SpecPhaseState.Demolished)]
        [InlineData("Past", SpecPhaseState.Past)]
        [InlineData("Future", SpecPhaseState.Future)]
        [InlineData("None", SpecPhaseState.Unknown)]
        [InlineData("", SpecPhaseState.Unknown)]
        [InlineData(null, SpecPhaseState.Unknown)]
        public void Revit_phase_status_names_map_to_states(string revit, SpecPhaseState expected)
        {
            Assert.Equal(expected, SpecExtractionRules.PhaseOf(revit));
        }

        [Fact]
        public void Unknown_phase_is_kept_so_levels_and_grids_are_never_dropped()
        {
            Assert.Null(SpecExtractionRules.ExcludeReason(false, SpecDesignOptionKind.None, SpecPhaseState.Unknown));
        }

        // ── missing vs empty ──

        [Fact]
        public void Classify_separates_missing_empty_and_filled()
        {
            Assert.Equal(SpecParamState.Missing, SpecExtractionRules.Classify(false, "ignored").State);
            Assert.Equal(SpecParamState.Empty, SpecExtractionRules.Classify(true, "").State);
            Assert.Equal(SpecParamState.Empty, SpecExtractionRules.Classify(true, "  ").State);
            Assert.Equal(SpecParamState.Empty, SpecExtractionRules.Classify(true, null).State);
            var f = SpecExtractionRules.Classify(true, "DGb");
            Assert.Equal(SpecParamState.Filled, f.State);
            Assert.Equal("DGb", f.Text);
        }

        private static SpecType Door(bool read, params string[] carriedNonText)
        {
            var t = new SpecType { TypeId = 1, Category = "Doors", TypeName = "(PTc080a) 1800 x 2100", InstanceCount = 2, InstanceIds = { 11, 12 }, ParamNamesRead = read };
            t.ParamNames.AddRange(carriedNonText);
            return t;
        }

        private static void CarryEveryRequiredDoorParameter(SpecType t)
        {
            foreach (var p in SpecPbParameters.At("Door", 400).Where(x => x.IsShared && !x.Starred)) t.ParamNames.Add(p.Name);
        }

        private static SpecModel At400(SpecType t)
        {
            var m = new SpecModel { Lod = 400 };
            m.Types.Add(t);
            return m.Normalize();
        }

        [Fact]
        public void Db1_a_mandatory_parameter_missing_from_the_type_fails_and_is_named_missing_not_empty()
        {
            var t = Door(true);
            t.Texts["Kod_Jenis_jkr_stt"] = "PTc";
            t.Texts["Pembekal_jkr_sit"] = "";            // empty
            // every other mandatory parameter is absent from Texts and from ParamNames: missing
            var f = SpecChecks.Rules["Db.1"](At400(t)).Single(r => r.Status == "fail");
            Assert.Contains("kosong: Pembekal_jkr_sit", f.Note);
            Assert.Contains("tiada pada type:", f.Note);
            Assert.DoesNotContain("Kod_Jenis_jkr_stt", f.Note);
            Assert.StartsWith("kosong: Pembekal_jkr_sit; tiada:", f.Actual);
        }

        [Fact]
        public void Db1_only_empty_gives_the_old_wording_without_a_missing_part()
        {
            var t = Door(true);
            CarryEveryRequiredDoorParameter(t);   // carried as non-text: not judged
            t.Texts["Kod_Jenis_jkr_stt"] = "";
            t.Texts["Pembekal_jkr_sit"] = "Greatech";
            var f = SpecChecks.Rules["Db.1"](At400(t)).Single(r => r.Status == "fail");
            Assert.Contains("1/2", f.Note);                       // carried as non-text ones are not judged
            Assert.Contains("kosong: Kod_Jenis_jkr_stt", f.Note);
            Assert.DoesNotContain("tiada pada type", f.Note);
        }

        [Fact]
        public void Db1_an_unread_snapshot_still_judges_only_what_its_texts_hold()
        {
            var t = Door(false);                                   // ParamNamesRead false: old snapshot
            var rs = SpecChecks.Rules["Db.1"](At400(t)).ToList();
            Assert.Empty(rs);                                      // nothing in Texts: not judged, not missing
        }

        [Fact]
        public void Db1_a_type_with_every_mandatory_parameter_filled_passes()
        {
            var t = Door(true);
            foreach (var p in SpecPbParameters.At("Door", 400).Where(x => x.IsShared && !x.Starred))
            {
                if (p.IsTextValued) t.Texts[p.Name] = "x"; else t.ParamNames.Add(p.Name);
            }
            var rs = SpecChecks.Rules["Db.1"](At400(t)).ToList();
            Assert.DoesNotContain(rs, r => r.Status == "fail");
            Assert.Single(rs, r => r.Status == "pass" && r.Coverage == SpecCoverage.FailOnly);
        }
    }
}
