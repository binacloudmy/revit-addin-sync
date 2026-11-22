#nullable enable
// AI-530 (WP-8): the Revit-free half of bind_parameters. Arguments, the spec's category map and the plan.
// The Revit calls (open the shared parameter file, Insert / ReInsert, the read-back) cannot run here: they are
// checked on a Revit test box (see the PR body for what is and is not verified).

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.Services.JkrSpec;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class BindParametersTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static BindParametersArgs Args(string json) => BindParametersArgs.Parse(Json(json));

        private static readonly BindSpecRow[] Spec =
        {
            new BindSpecRow("Kod_Jenis_jkr_stt", "Door", false),
            new BindSpecRow("Kod_Jenis_jkr_stt", "Window", false),
            new BindSpecRow("Kod_Jenis_jkr_stt", "Column", false),
            new BindSpecRow("Kaedah_Pemasangan_jkr_sit", "Door", true),
            new BindSpecRow("Luas_Brif_jkr_sia", "Area", true),
        };

        private static BindingState Missing(bool inFile = true) => new BindingState { InFile = inFile };

        private static BindingState Bound(bool instance, params string[] cats) =>
            new BindingState { InFile = true, Bound = true, BoundAsInstance = instance, BoundCategories = cats };

        private static BindItem One(string json, BindingState state, params BindSpecRow[] spec) =>
            BindPlanner.Plan(Args(json), spec.Length == 0 ? Spec : spec, _ => state).Single();

        // ---- arguments -------------------------------------------------------

        [Fact]
        public void Parameters_are_trimmed_deduplicated_and_dry_run_defaults_to_false()
        {
            var a = Args("{\"parameters\":[\" Kod_Jenis_jkr_stt \",\"kod_jenis_jkr_stt\",\"Luas_Brif_jkr_sia\"]}");
            Assert.Equal(new[] { "Kod_Jenis_jkr_stt", "Luas_Brif_jkr_sia" }, a.Parameters);
            Assert.Empty(a.Categories);
            Assert.False(a.DryRun);
            Assert.True(Args("{\"parameters\":[\"A_jkr_sit\"],\"dry_run\":true,\"categories\":[\"Doors\"]}").DryRun);
            Assert.Equal(new[] { "Doors" }, Args("{\"parameters\":[\"A_jkr_sit\"],\"categories\":[\"Doors\"]}").Categories);
        }

        [Theory]
        [InlineData("{}", "Give parameters")]
        [InlineData("{\"parameters\":[]}", "Give parameters")]
        [InlineData("[]", "JSON object")]
        [InlineData("{\"parameters\":\"Kod_Jenis_jkr_stt\"}", "list of names")]
        [InlineData("{\"parameters\":[\"\"]}", "blank")]
        [InlineData("{\"parameters\":[3]}", "blank or non-text")]
        [InlineData("{\"parameters\":[\"Name\"]}", "Only JKR shared parameters")]
        [InlineData("{\"parameters\":[\"Comments\",\"Kod_Jenis_jkr_stt\"]}", "Not accepted: Comments")]
        [InlineData("{\"parameters\":[\"Kod_Jenis_jkr_stz\"]}", "Only JKR shared parameters")]
        [InlineData("{\"parameters\":[\"A_jkr_sit\"],\"dry_run\":\"yes\"}", "true or false")]
        [InlineData("{\"parameters\":[\"A_jkr_sit\"],\"create\":true}", "Unknown argument 'create'")]
        [InlineData("{\"parameters\":[\"A_jkr_sit\"],\"definition\":{\"name\":\"X\"}}", "Unknown argument 'definition'")]
        [InlineData("{\"parameters\":[\"A_jkr_sit\"],\"categories\":\"Doors\"}", "list of names")]
        public void Every_problem_is_invalid_args_with_its_reason(string json, string reason)
        {
            var ex = Assert.Throws<WriteRefusal>(() => Args(json));
            Assert.Equal(ErrorCodes.InvalidArgs, ex.Code);
            Assert.Contains(reason, ex.Message);
        }

        [Fact]
        public void More_than_forty_parameters_are_refused()
        {
            var names = string.Join(",", Enumerable.Range(0, BindParametersArgs.MaxParameters + 1).Select(n => "\"P" + n + "_jkr_sit\""));
            var ex = Assert.Throws<WriteRefusal>(() => Args("{\"parameters\":[" + names + "]}"));
            Assert.Contains("at most 40", ex.Message);
            Assert.Single(Args("{\"parameters\":[\"A_jkr_sit\"]}").Parameters);
        }

        [Theory]
        [InlineData("Kod_Jenis_jkr_stt", true)]
        [InlineData("Luas_Brif_jkr_sia", true)]
        [InlineData("Name", false)]
        [InlineData("Base Level", false)]
        [InlineData("X_jkr_st", false)]
        public void Only_a_jkr_shared_parameter_name_is_accepted(string name, bool jkr) => Assert.Equal(jkr, BindParametersArgs.IsJkrName(name));

        [Fact]
        public void Every_shared_parameter_name_in_the_spec_table_is_accepted()
        {
            foreach (var p in SpecPbParameters.All.Where(x => x.IsShared))
                Assert.True(BindParametersArgs.IsJkrName(p.Name), p.Name);
        }

        // ---- category map ----------------------------------------------------

        [Fact]
        public void Every_book_category_of_the_spec_has_a_revit_category_so_none_is_dropped_silently()
        {
            foreach (var cat in SpecPbParameters.All.Select(p => p.Category).Distinct())
                Assert.True(BindCategoryMap.For(cat).Count > 0, "no Revit category for book category '" + cat + "'");
            foreach (var cat in SpecPbData.ArCategories) Assert.True(BindCategoryMap.For(cat.Name).Count > 0, cat.Name);
        }

        [Fact]
        public void The_map_names_real_builtin_categories_only_once_each_per_book_category()
        {
            foreach (var book in BindCategoryMap.BookCategories)
            {
                var built = BindCategoryMap.For(book).Select(c => c.Built).ToList();
                Assert.Equal(built.Count, built.Distinct().Count());
                Assert.All(built, b => Assert.StartsWith("OST_", b));
            }
            Assert.Equal(new[] { "OST_Columns", "OST_StructuralColumns" }, BindCategoryMap.For("Column").Select(c => c.Built));
        }

        // ---- plan ------------------------------------------------------------

        [Fact]
        public void An_unbound_parameter_is_bound_to_every_spec_category_with_the_spec_kind()
        {
            var item = One("{\"parameters\":[\"Kod_Jenis_jkr_stt\"]}", Missing());
            Assert.Equal(BindStatus.Bind, item.Status);
            Assert.False(item.Instance);   // the spec lists it as a type parameter
            Assert.Equal(new[] { "OST_Doors", "OST_Windows", "OST_Columns", "OST_StructuralColumns" }, item.ToAdd);
            Assert.True(item.Changes);
        }

        [Fact]
        public void The_spec_kind_comes_from_the_name_and_an_instance_parameter_is_bound_as_instance()
        {
            var item = One("{\"parameters\":[\"Kaedah_Pemasangan_jkr_sit\"]}", Missing());
            Assert.True(item.Instance);
            Assert.Equal(new[] { "OST_Doors" }, item.ToAdd);
        }

        [Fact]
        public void A_parameter_bound_to_some_categories_is_extended_with_the_rest_only()
        {
            var item = One("{\"parameters\":[\"Kod_Jenis_jkr_stt\"]}", Bound(false, "OST_Doors", "OST_Walls"));
            Assert.Equal(BindStatus.Extend, item.Status);
            Assert.Equal(new[] { "OST_Windows", "OST_Columns", "OST_StructuralColumns" }, item.ToAdd);
            Assert.Equal(new[] { "Doors" }, item.AlreadyBoundTo);
        }

        [Fact]
        public void A_parameter_that_already_covers_every_wanted_category_is_left_alone()
        {
            var item = One("{\"parameters\":[\"Kod_Jenis_jkr_stt\"]}", Bound(false, "OST_Doors", "OST_Windows", "OST_Columns", "OST_StructuralColumns", "OST_Walls"));
            Assert.Equal(BindStatus.AlreadyBound, item.Status);
            Assert.False(item.Changes);
            Assert.Empty(item.ToAdd);
        }

        [Fact]
        public void A_definition_missing_from_the_jkr_file_is_refused_and_never_created()
        {
            var item = One("{\"parameters\":[\"Kod_Jenis_jkr_stt\"]}", Missing(inFile: false));
            Assert.Equal(BindStatus.NotInFile, item.Status);
            Assert.False(item.Changes);
            Assert.Contains("never creates a definition", item.Reason);
        }

        [Fact]
        public void A_parameter_the_spec_does_not_list_has_no_category_and_is_not_bound()
        {
            var item = One("{\"parameters\":[\"Unlisted_jkr_sit\"]}", Missing());
            Assert.Equal(BindStatus.NotInSpec, item.Status);
            Assert.False(item.Changes);
        }

        [Fact]
        public void An_existing_binding_of_the_other_kind_is_reported_and_never_flipped()
        {
            var item = One("{\"parameters\":[\"Kod_Jenis_jkr_stt\"]}", Bound(instance: true, "OST_Doors"));
            Assert.Equal(BindStatus.KindConflict, item.Status);
            Assert.False(item.Changes);
            Assert.Contains("instance", item.Reason);
            Assert.Contains("type", item.Reason);
        }

        [Fact]
        public void A_different_parameter_with_the_same_name_in_the_project_is_a_name_clash_not_a_second_bind()
        {
            var item = One("{\"parameters\":[\"Kod_Jenis_jkr_stt\"]}", new BindingState { InFile = true, NameTaken = true });
            Assert.Equal(BindStatus.NameConflict, item.Status);
            Assert.False(item.Changes);
        }

        [Theory]
        [InlineData("Doors")]
        [InlineData("OST_Doors")]
        [InlineData("door")]
        public void Categories_can_only_narrow_what_the_spec_lists(string narrow)
        {
            var item = One("{\"parameters\":[\"Kod_Jenis_jkr_stt\"],\"categories\":[\"" + narrow + "\"]}", Missing());
            Assert.Equal(BindStatus.Bind, item.Status);
            Assert.Equal(new[] { "OST_Doors" }, item.ToAdd);
        }

        [Fact]
        public void A_category_the_spec_does_not_list_for_the_parameter_cannot_widen_it()
        {
            var item = One("{\"parameters\":[\"Kod_Jenis_jkr_stt\"],\"categories\":[\"Walls\"]}", Missing());
            Assert.Equal(BindStatus.NoWantedCategory, item.Status);
            Assert.False(item.Changes);
            Assert.Empty(item.ToAdd);
        }

        [Fact]
        public void The_plan_has_one_item_per_requested_parameter_in_request_order()
        {
            var plan = BindPlanner.Plan(Args("{\"parameters\":[\"Luas_Brif_jkr_sia\",\"Missing_jkr_sit\",\"Kod_Jenis_jkr_stt\"]}"), Spec, n => n == "Kod_Jenis_jkr_stt" ? Bound(false, "OST_Doors", "OST_Windows", "OST_Columns", "OST_StructuralColumns") : Missing());
            Assert.Equal(new[] { "Luas_Brif_jkr_sia", "Missing_jkr_sit", "Kod_Jenis_jkr_stt" }, plan.Select(p => p.Name));
            Assert.Equal(new[] { BindStatus.Bind, BindStatus.NotInSpec, BindStatus.AlreadyBound }, plan.Select(p => p.Status));
        }

        [Fact]
        public void Planning_twice_gives_the_same_plan_so_the_commit_re_plan_matches_the_preview()
        {
            string Key(IEnumerable<BindItem> items) => string.Join("|", items.Select(i => i.Name + ":" + i.Status + ":" + string.Join(",", i.ToAdd)));
            var a = BindPlanner.Plan(Args("{\"parameters\":[\"Kod_Jenis_jkr_stt\",\"Kaedah_Pemasangan_jkr_sit\"]}"), Spec, _ => Missing());
            var b = BindPlanner.Plan(Args("{\"parameters\":[\"Kod_Jenis_jkr_stt\",\"Kaedah_Pemasangan_jkr_sit\"]}"), Spec, _ => Missing());
            Assert.Equal(Key(a), Key(b));
        }

        [Fact]
        public void The_real_spec_table_plans_a_jkr_door_parameter_to_doors()
        {
            var rows = SpecPbParameters.All.Where(p => p.IsShared).Select(p => new BindSpecRow(p.Name, p.Category, p.IsInstance)).ToList();
            var name = SpecPbParameters.ForBookCategory("Door").First(p => p.IsShared).Name;
            var item = BindPlanner.Plan(Args("{\"parameters\":[\"" + name + "\"]}"), rows, _ => Missing()).Single();
            Assert.Equal(BindStatus.Bind, item.Status);
            Assert.Contains("OST_Doors", item.ToAdd);
        }

        [Fact]
        public void The_stub_file_this_add_in_provisions_for_its_older_auto_fix_is_not_the_official_file()
        {
            Assert.True(BindParametersText.IsProvisionedStub("# This is a Revit shared parameter file.\n# Provisioned by BINA JKR auto-fix.\n*META\tVERSION\tMINVERSION"));
            Assert.False(BindParametersText.IsProvisionedStub("# This is a Revit shared parameter file.\n# Do not edit manually.\n*META\tVERSION\tMINVERSION"));
            Assert.False(BindParametersText.IsProvisionedStub(null));
        }
    }
}
