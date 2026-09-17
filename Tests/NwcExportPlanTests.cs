using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The Revit-to-NWC checklist, as data (ClickUp 86d49v9ak).
    ///
    /// These values came off two hand-annotated PDFs and a screenshot; they are
    /// not derivable from anything and a silent change to one of them produces a
    /// cache that loads in Navisworks and is quietly wrong — wrong origin, missing
    /// element ids, doubled geometry from links. Pinned here for that reason.
    /// </summary>
    public class NwcExportPlanTests
    {
        private static NwcExportPlan Plan(
            NwcCoordinates coordinates = NwcCoordinates.Shared,
            bool purge = false,
            bool purgeSupported = true,
            string modelFileName = "Block-A.rvt")
            => NwcExportPlan.From(
                new NwcExportSettings { Coordinates = coordinates, PurgeUnused = purge },
                modelFileName,
                purgeSupported);

        [Fact]
        public void Mapping_table_matches_the_checklist()
        {
            var plan = Plan();

            // Export = Current view, on the temporary view this add-in creates.
            Assert.True(plan.ExportCurrentViewOnly);
            // Element IDs ticked — this is what ties a clash back to an element.
            Assert.True(plan.ExportElementIds);
            // "Convert linked files" unticked: each discipline exports its own.
            Assert.False(plan.ExportLinks);

            Assert.True(plan.ConvertLinkedCADFormats);
            Assert.True(plan.ExportRoomAsAttribute);
            Assert.True(plan.ExportRoomGeometry);
            Assert.True(plan.DivideFileIntoLevels);
            Assert.True(plan.ExportUrls);
            Assert.False(plan.ExportParts);
            Assert.False(plan.ConvertElementProperties);
            Assert.False(plan.ConvertLights);
            Assert.Equal(1.0, plan.FacetingFactor);
            Assert.True(plan.FindMissingMaterials);
            Assert.Equal("All", plan.Parameters);
        }

        [Fact]
        public void View_rules_match_the_checklist()
        {
            var plan = Plan();

            Assert.Equal("Fine", plan.ViewDetailLevel);
            Assert.True(plan.ViewIsIsometric);
            Assert.False(plan.ViewSectionBoxEnabled);
        }

        [Fact]
        public void Coordinates_default_to_shared()
        {
            // Rev a (newer, hand-annotated) says Shared; TCDR01 says Project
            // Internal. If this ever flips, every model federates in the wrong
            // place and nothing in the file says why.
            Assert.Equal(NwcCoordinates.Shared, Plan().Coordinates);
            Assert.Equal(NwcCoordinates.Shared, NwcExportPlan.From(new NwcExportSettings(), "x.rvt", true).Coordinates);
        }

        [Fact]
        public void Coordinates_follow_the_choice()
        {
            Assert.Equal(NwcCoordinates.Internal, Plan(coordinates: NwcCoordinates.Internal).Coordinates);
        }

        [Fact]
        public void Hides_exactly_annotation_analytical_imported_and_links()
        {
            var hidden = Plan().HiddenCategoryGroups;

            Assert.Contains(NwcHiddenCategoryGroup.Annotation, hidden);
            Assert.Contains(NwcHiddenCategoryGroup.AnalyticalModel, hidden);
            Assert.Contains(NwcHiddenCategoryGroup.Imported, hidden);
            Assert.Contains(NwcHiddenCategoryGroup.RevitLinks, hidden);
            // Four groups and no more: model categories stay visible, which is
            // the entire point of the export.
            Assert.Equal(4, hidden.Count);
        }

        [Theory]
        // The version suffix a Cloud Docs download carries has to come off, or the
        // export is linked beside the previous one instead of replacing it.
        [InlineData("Model-v3.rvt", "Model.nwc")]
        [InlineData("Model_v12.rvt", "Model.nwc")]
        [InlineData("Model-V3.RVT", "Model.nwc")]
        [InlineData("Block-A.rvt", "Block-A.nwc")]
        // Not a version suffix: a real name that happens to end in a digit.
        [InlineData("Level-3.rvt", "Level-3.nwc")]
        [InlineData("Tower v2 Block-A.rvt", "Tower v2 Block-A.nwc")]
        public void File_name_takes_the_model_stem(string model, string expected)
        {
            Assert.Equal(expected, NwcFileName.FromModelName(model));
        }

        [Theory]
        // Revit model and view names accept characters a path does not, and
        // Document.Export throws on them rather than cleaning them up.
        [InlineData("Block A*B?.rvt", "Block A_B_.nwc")]
        [InlineData("Block\"A.rvt", "Block_A.nwc")]
        [InlineData("Block|A.rvt", "Block_A.nwc")]
        public void File_name_replaces_characters_windows_refuses(string model, string expected)
        {
            Assert.Equal(expected, NwcFileName.FromModelName(model));
        }

        [Fact]
        public void File_name_never_ends_in_a_dot_or_space()
        {
            Assert.Equal("Block-A.nwc", NwcFileName.FromModelName("Block-A .rvt"));
            Assert.Equal("Block-A.nwc", NwcFileName.FromModelName("Block-A..rvt"));
        }

        [Fact]
        public void File_name_falls_back_rather_than_producing_a_bare_extension()
        {
            // A stem that cleans up to nothing must not become ".nwc" — a hidden
            // file on every platform and an empty name on the server.
            Assert.Equal("model.nwc", NwcFileName.FromModelName("   .rvt"));
        }

        [Fact]
        public void Purge_runs_only_when_asked_for_and_possible()
        {
            var asked = Plan(purge: true, purgeSupported: true);
            Assert.True(asked.PurgeUnused);
            Assert.Null(asked.PurgeSkippedNote);

            var notAsked = Plan(purge: false, purgeSupported: true);
            Assert.False(notAsked.PurgeUnused);
            Assert.Null(notAsked.PurgeSkippedNote);
        }

        [Fact]
        public void Purge_request_is_dropped_with_a_reason_where_the_api_is_absent()
        {
            var plan = Plan(purge: true, purgeSupported: false);

            // Silently ignoring it would leave the drafter believing the model was
            // purged. The note is what the dialog and the outcome both say.
            Assert.False(plan.PurgeUnused);
            Assert.Equal(NwcPurgeSupport.UnsupportedNote, plan.PurgeSkippedNote);
        }

        [Fact]
        public void Purge_is_not_offered_where_it_was_never_asked_for()
        {
            Assert.Null(Plan(purge: false, purgeSupported: false).PurgeSkippedNote);
        }

        [Theory]
        // Document.GetUnusedElements is 2024 API, but the net48 payload compiles
        // against 2023 refs and serves 2023 AND 2024 — so 2024 cannot have it
        // either, whatever the host reports. 2025+ ship on net8/net10.
        [InlineData("2023", false)]
        [InlineData("2024", false)]
        [InlineData("2025", true)]
        [InlineData("2026", true)]
        [InlineData("2027", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("not a year", false)]
        public void Purge_support_is_a_2025_and_later_capability(string revitVersion, bool supported)
        {
            Assert.Equal(supported, NwcPurgeSupport.IsSupported(revitVersion));
        }

        [Fact]
        public void Purge_is_compiled_in_on_this_target()
        {
            // The Tests project targets net10, which is the Revit 2027 payload —
            // so the constant that gates the call must be on here. On net48 the
            // same constant is false and the #if in NwcExporter removes the call
            // entirely; that branch cannot be exercised from this TFM, which is
            // exactly why the plan takes the flag as an argument.
            Assert.True(NwcPurgeSupport.CompiledIn);
        }
    }
}
