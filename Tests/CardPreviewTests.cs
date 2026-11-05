// AI-220: the approval card is built from the plan. Pure logic — the plan is a
// plain dictionary, exactly the shape the dry run returns from Revit.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CardPreviewTests
    {
        private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static PendingToolCall Call(string tool, string argsJson, bool mutate = true) =>
            new PendingToolCall { ToolCallId = "tc-" + tool, Tool = tool, Args = J(argsJson), Mutate = mutate };

        private static Dictionary<string, object> Plan(int matched, int would, params (string from, string to)[] rows) =>
            new Dictionary<string, object>
            {
                ["ok"] = true, ["dry_run"] = true, ["matched"] = matched, ["would_set"] = would,
                ["unchanged"] = 0, ["read_only"] = 0, ["grouped_skipped"] = 0, ["missing"] = 0,
                ["preview"] = rows.Select(r => (object)new Dictionary<string, object> { ["from"] = r.from, ["to"] = r.to }).ToList(),
            };

        private const string SetArgs = "{\"category\":\"Doors\",\"parameter\":\"Comments\",\"value\":\"semak\",\"predicate\":\"level=Aras 02\"}";

        // ── multi-target ────────────────────────────────────────────────────

        [Fact]
        public void SetByFilter_ShowsCountScopeAndOldNewFromThePlan()
        {
            var line = CardPreview.Line(Call("set_parameter_by_filter", SetArgs),
                Plan(46, 46, ("", "semak"), ("", "semak")));
            Assert.Contains("Set Comments to \"semak\" on 46 Doors", line);
            Assert.Contains("where level=Aras 02", line);
            Assert.Contains("was empty", line);
            Assert.EndsWith("(46 of 46)", line);   // the pane right-docks this as the metric
        }

        [Fact]
        public void SetByFilter_CountComesFromThePlanNotTheArguments()
        {
            // Same arguments, a different plan: the row follows the plan.
            var a = CardPreview.Line(Call("set_parameter_by_filter", SetArgs), Plan(46, 46, ("", "x")));
            var b = CardPreview.Line(Call("set_parameter_by_filter", SetArgs), Plan(31, 29, ("", "x")));
            Assert.Contains("on 46 Doors", a);
            Assert.Contains("on 29 Doors", b);
            Assert.EndsWith("(29 of 31)", b);
        }

        [Fact]
        public void SetByFilter_SaysWhatIsSkippedAndWhy()
        {
            var plan = Plan(50, 40, ("old", "semak"));
            plan["unchanged"] = 6; plan["grouped_skipped"] = 3; plan["read_only"] = 1;
            var line = CardPreview.Line(Call("set_parameter_by_filter", SetArgs), plan);
            Assert.Contains("6 already set", line);
            Assert.Contains("3 in groups, skipped", line);
            Assert.Contains("1 read-only, skipped", line);
            Assert.Contains("was \"old\"", line);
            Assert.EndsWith("(40 of 50)", line);
        }

        [Fact]
        public void SetByFilter_ManyDifferentCurrentValues_AreCounted()
        {
            var line = CardPreview.Line(Call("set_parameter_by_filter", SetArgs),
                Plan(3, 3, ("a", "x"), ("b", "x"), ("c", "x")));
            Assert.Contains("3 different current values", line);
        }

        // ── zero-target ─────────────────────────────────────────────────────

        [Fact]
        public void SetByFilter_NothingMatched_SaysSo()
        {
            var line = CardPreview.Line(Call("set_parameter_by_filter", SetArgs), Plan(0, 0));
            Assert.StartsWith("Nothing will change: no Doors", line);
            Assert.DoesNotContain("(0 of 0)", line);
        }

        [Fact]
        public void SetByFilter_AllAlreadySet_SaysNothingWillChange()
        {
            var plan = Plan(46, 0);
            plan["unchanged"] = 46;
            var line = CardPreview.Line(Call("set_parameter_by_filter", SetArgs), plan);
            Assert.StartsWith("Nothing will change: 46 Doors", line);
            Assert.Contains("46 already set", line);
        }

        // ── the call cannot run as written ──────────────────────────────────

        [Fact]
        public void SetByFilter_TypeParameter_WarnsItChangesTypesModelWide()
        {
            var plan = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "'Fire Rating' is a type parameter",
                ["type_parameter"] = new Dictionary<string, object>
                {
                    ["parameter"] = "Fire Rating", ["instances_matched"] = 46,
                    ["types"] = new List<object>
                    {
                        new Dictionary<string, object> { ["type"] = "D1", ["instances_in_model"] = 80 },
                        new Dictionary<string, object> { ["type"] = "D2", ["instances_in_model"] = 40 },
                    },
                },
            };
            var line = CardPreview.Line(Call("set_parameter_by_filter",
                "{\"category\":\"Doors\",\"parameter\":\"Fire Rating\",\"value\":\"1hr\"}"), plan);
            Assert.StartsWith("Blocked: Fire Rating is a TYPE parameter", line);
            Assert.Contains("2 types", line);
            Assert.Contains("120 Doors in the whole model", line);
        }

        [Fact]
        public void SetByFilter_RefusedCall_ShowsTheErrorAndSuggestions()
        {
            var plan = new Dictionary<string, object>
            {
                ["ok"] = false, ["error"] = "level 'Aras 2' not found",
                ["candidates"] = new List<object> { "Aras 01", "Aras 02", "Bumbung", "Tangki" },
            };
            var line = CardPreview.Line(Call("set_parameter_by_filter", SetArgs), plan);
            Assert.StartsWith("Will not run: level 'Aras 2' not found", line);
            Assert.Contains("did you mean Aras 01, Aras 02, Bumbung?", line);
        }

        [Fact]
        public void SetByFilter_NoPlan_SaysTheCountIsNotKnownYet_AndShowsNoNumber()
        {
            var line = CardPreview.Line(Call("set_parameter_by_filter", SetArgs), null);
            Assert.Contains("count checked by Revit when it runs", line);
            Assert.DoesNotContain("(", line);
        }

        // ── change vs create ────────────────────────────────────────────────

        [Fact]
        public void SwapType_ShowsTheFromTypesAndHowManyChange()
        {
            var plan = new Dictionary<string, object>
            {
                ["ok"] = true, ["matched"] = 20, ["would_swap"] = 17, ["unchanged"] = 3,
                ["preview"] = Enumerable.Repeat((object)new Dictionary<string, object> { ["from"] = "M_Single 0915" }, 12)
                    .Concat(Enumerable.Repeat((object)new Dictionary<string, object> { ["from"] = "M_Single 0900" }, 5)).ToList(),
            };
            var line = CardPreview.Line(Call("swap_type_by_filter",
                "{\"category\":\"Doors\",\"type_name\":\"M_Single 1000\"}"), plan);
            Assert.StartsWith("Change type of 17 Doors to M_Single 1000", line);
            Assert.Contains("12 × M_Single 0915", line);
            Assert.Contains("5 × M_Single 0900", line);
            Assert.Contains("3 already this type", line);
            Assert.EndsWith("(17 of 20)", line);
        }

        [Fact]
        public void CreateAndChange_NeverReadTheSame()
        {
            var create = CardPreview.Line(Call("create_ceiling",
                "{\"level\":\"PTBR_Aras 01\",\"type_name\":\"Compound 600\"}"), null);
            var change = CardPreview.Line(Call("set_parameter_by_filter",
                "{\"category\":\"Ceilings\",\"parameter\":\"Height Offset From Level\",\"value\":\"2700\"}"),
                Plan(31, 31, ("3999", "2700")));
            Assert.StartsWith("Create a ceiling on PTBR_Aras 01", create);
            Assert.Contains("type Compound 600", create);
            Assert.StartsWith("Set Height Offset From Level to \"2700\" on 31 Ceilings", change);
            Assert.DoesNotContain("Create", change);
        }

        [Fact]
        public void CreateCeiling_WithoutType_SaysTheTypeWasNotChosen()
        {
            var line = CardPreview.Line(Call("create_ceiling", "{\"level\":\"PTBR_Aras 01\"}"), null);
            Assert.Contains("type not chosen", line);
        }

        // ── tools without a dry run: rows come from their own arguments ─────

        [Fact]
        public void SetTypeParameter_SaysItEditsTheTypeForEveryInstance()
        {
            var line = CardPreview.Line(Call("set_type_parameter",
                "{\"category\":\"Doors\",\"type_name\":\"M_Single 0915\",\"param\":\"Fire Rating\",\"value\":\"1hr\"}"), null);
            Assert.StartsWith("Edit TYPE M_Single 0915 (Doors): Fire Rating = \"1hr\"", line);
            Assert.Contains("every Doors of this type in the model", line);
        }

        [Fact]
        public void BulkAndDelete_CountTheIdsInTheCall()
        {
            Assert.Equal("Set Mark to \"A\" on 3 elements",
                CardPreview.Line(Call("set_parameter_bulk", "{\"element_ids\":[1,2,3],\"param\":\"Mark\",\"value\":\"A\"}"), null));
            Assert.Equal("Delete 1 element",
                CardPreview.Line(Call("delete_elements", "{\"element_ids\":[7]}"), null));
            Assert.Equal("Delete 0 elements",
                CardPreview.Line(Call("delete_elements", "{\"element_ids\":[]}"), null));
        }

        [Fact]
        public void Renumber_SaysItReplacesExistingValues()
        {
            var line = CardPreview.Line(Call("renumber_elements",
                "{\"category\":\"Doors\",\"parameter\":\"Mark\",\"start\":101,\"prefix\":\"D-\",\"level\":\"Aras 01\"}"), null);
            Assert.StartsWith("Renumber Mark on every Doors on Aras 01 from D-101", line);
            Assert.Contains("replaces the current Mark values", line);
        }

        [Fact]
        public void Sheets_ListTheNumbers_AndShortenLongLists()
        {
            Assert.Equal("Create 2 sheets · A101, A102",
                CardPreview.Line(Call("create_sheets_batch", "{\"rows\":[{\"number\":\"A101\"},{\"number\":\"A102\"}]}"), null));
            var five = CardPreview.Line(Call("create_sheets_batch",
                "{\"rows\":[{\"number\":\"A101\"},{\"number\":\"A102\"},{\"number\":\"A103\"},{\"number\":\"A104\"},{\"number\":\"A105\"}]}"), null);
            Assert.Equal("Create 5 sheets · A101 … A105", five);
        }

        // ── fallbacks and the dry-run copy ──────────────────────────────────

        [Fact]
        public void UnknownTool_KeepsThePlainLabel()
        {
            var c = Call("create_wall", "{\"level\":\"Aras 01\"}");
            Assert.Equal(ToolLabels.Label("create_wall", c.Args), CardPreview.Line(c, null));
        }

        [Fact]
        public void MissingArguments_NeverThrow()
        {
            foreach (var tool in new[] { "set_parameter_by_filter", "swap_type_by_filter", "set_type_parameter",
                                          "set_parameter_bulk", "delete_elements", "renumber_elements",
                                          "create_ceiling", "create_floor", "create_sheets_batch" })
            {
                var c = new PendingToolCall { Tool = tool };      // Args = default(JsonElement)
                Assert.False(string.IsNullOrWhiteSpace(CardPreview.Line(c, null)));
                Assert.False(string.IsNullOrWhiteSpace(CardPreview.Line(c, new Dictionary<string, object>())));
            }
        }

        [Fact]
        public void Rows_UseThePlanRow_AndFallBackPerCall()
        {
            var pending = new List<PendingToolCall>
            {
                Call("set_parameter_by_filter", SetArgs), Call("create_wall", "{}"), Call("delete_elements", "{\"element_ids\":[1]}"),
            };
            var rows = CardPreview.Rows(pending, new List<string> { "PLAN ROW", "" });   // shorter than the batch, one empty
            Assert.Equal(3, rows.Count);
            Assert.Equal("PLAN ROW", rows[0]);
            Assert.Equal(ToolLabels.Label("create_wall", pending[1].Args), rows[1]);
            Assert.Equal(ToolLabels.Label("delete_elements", pending[2].Args), rows[2]);
            Assert.Empty(CardPreview.Rows(null, null));
        }

        [Fact]
        public void DryRunCopy_IsARead_WithTheSameArguments_AndDryRunTrue()
        {
            var call = Call("set_parameter_by_filter",
                "{\"category\":\"Doors\",\"parameter\":\"Comments\",\"value\":\"x\",\"dry_run\":false}");
            call.IdempotencyKey = "abc"; call.RequiresConfirmation = true; call.ExpectedRevision = 12;
            var copy = CardPreview.DryRunCopy(call);
            Assert.False(copy.Mutate);
            Assert.False(copy.RequiresConfirmation);
            Assert.Equal("", copy.IdempotencyKey);
            Assert.Null(copy.ExpectedRevision);
            Assert.Equal("set_parameter_by_filter", copy.Tool);
            Assert.True(copy.Args.GetProperty("dry_run").GetBoolean());
            Assert.Equal("Doors", copy.Args.GetProperty("category").GetString());
            Assert.Equal("x", copy.Args.GetProperty("value").GetString());
            // the original call is untouched
            Assert.False(call.Args.GetProperty("dry_run").GetBoolean());
            Assert.True(call.Mutate);
        }

        [Fact]
        public void OnlyTheTwoPlanTools_AreDryRun()
        {
            Assert.True(CardPreview.HasDryRun("set_parameter_by_filter"));
            Assert.True(CardPreview.HasDryRun("swap_type_by_filter"));
            Assert.False(CardPreview.HasDryRun("delete_elements"));
            Assert.False(CardPreview.HasDryRun("create_ceiling"));
            Assert.False(CardPreview.HasDryRun("execute_revit_batch"));
        }
    }
}
