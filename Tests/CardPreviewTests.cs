// AI-220: the approval card is built from the plan. Pure logic — the plan is a
// plain dictionary, exactly the shape the dry run returns from Revit.

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

        // The pane right-docks a trailing "(…)" (ChatView.SplitLabelMetric). Only "(N of M)" is meant to.
        private static void AssertNoStrayMetric(string row)
        {
            Assert.False(string.IsNullOrWhiteSpace(row));
            if (row.EndsWith(")")) Assert.Matches(new Regex(@"\(\d+ of \d+\)$"), row);
        }

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

        // ── a preview that did not work: label + one short reason, never raw error text ──

        [Fact]
        public void FailedDryRun_ShortLevelName_FallsBackToTheLabel_NotTheDidYouMeanList()
        {
            // The exact case from the Revit test: the model said "Aras 02", the level is
            // "PTBR_Aras 02", Revit answered with an error and a candidates list.
            var plan = new Dictionary<string, object>
            {
                ["ok"] = false, ["error"] = "level 'Aras 02' not found",
                ["candidates"] = new List<object> { "PTBR_Aras Asas", "PTBR_Beam", "PTBR_Aras 01", "PTBR_Aras 02" },
                ["suggestions"] = new List<object> { "Mark", "Comments" },
            };
            var call = Call("set_parameter_by_filter",
                "{\"category\":\"Doors\",\"parameter\":\"Comments\",\"value\":\"semak\",\"predicate\":\"level=Aras 02\"}");
            var line = CardPreview.Line(call, plan);

            Assert.StartsWith(ToolLabels.Label("set_parameter_by_filter", call.Args), line);   // the old label first
            Assert.Contains("could not preview: level 'Aras 02' not found", line);
            Assert.DoesNotContain("did you mean", line);
            Assert.DoesNotContain("PTBR_Beam", line);
            Assert.DoesNotContain("PTBR_Aras Asas", line);
            Assert.DoesNotContain("\n", line);
            AssertNoStrayMetric(line);    // a trailing "(...)" is what made the pane draw an empty box
        }

        [Fact]
        public void FailedDryRun_ErrorWithParentheses_NeverEndsInAParen()
        {
            var plan = new Dictionary<string, object>
            {
                ["ok"] = false, ["error"] = "type 'X' is ambiguous (3 matches) — retry with the exact name (see list)",
                ["candidates"] = new List<object> { "A", "B", "C" },
            };
            foreach (var tool in new[] { "set_parameter_by_filter", "swap_type_by_filter", "set_type_parameter" })
            {
                var line = CardPreview.Line(Call(tool, "{\"category\":\"Doors\",\"type_name\":\"X\",\"parameter\":\"P\",\"value\":\"1\"}"), plan);
                Assert.Contains("could not preview", line);
                Assert.DoesNotContain("(", line.Substring(line.IndexOf("could not preview")));
                AssertNoStrayMetric(line);
            }
        }

        [Fact]
        public void FailedDryRun_NoErrorText_StillGivesAReason()
        {
            var line = CardPreview.Line(Call("swap_type_by_filter", "{\"category\":\"Doors\",\"type_name\":\"X\"}"),
                new Dictionary<string, object> { ["ok"] = false });
            Assert.Contains("could not preview: Revit refused the call", line);
        }

        [Fact]
        public void FailedDryRun_LongError_IsClippedToOneLine()
        {
            var plan = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "first line of the error that goes on and on and on and on and on and on and on and on\nsecond line stack trace",
            };
            var line = CardPreview.Line(Call("set_parameter_by_filter", SetArgs), plan);
            Assert.DoesNotContain("second line", line);
            Assert.DoesNotContain("\n", line);
            Assert.True(line.Length < 220);
        }

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
            // The card still has an Allow button, so it must not read "Blocked".
            Assert.StartsWith("Warning: Fire Rating is a TYPE parameter", line);
            Assert.DoesNotContain("Blocked", line);
            Assert.Contains("2 types", line);
            Assert.Contains("120 Doors in the whole model", line);
            AssertNoStrayMetric(line);
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
            AssertNoStrayMetric(line);
        }

        // ── type edits (A-07): single call ──────────────────────────────────

        [Fact]
        public void SetTypeParameter_WithPlan_SaysItEditsTheTypeAndHowFarItReaches()
        {
            var plan = new Dictionary<string, object>
            {
                ["ok"] = true, ["dry_run"] = true, ["type_name"] = "M_Single 0915", ["parameter"] = "Fire Rating",
                ["current"] = "30", ["new_value"] = "60", ["instances_affected"] = 46,
            };
            var line = CardPreview.Line(Call("set_type_parameter",
                "{\"category\":\"Doors\",\"type_name\":\"M_Single 0915\",\"param\":\"Fire Rating\",\"value\":\"60\"}"), plan);
            Assert.StartsWith("Edits the TYPE M_Single 0915 (Doors): Fire Rating = \"60\"", line);
            Assert.EndsWith("used by 46 Doors in the whole model", line);
            AssertNoStrayMetric(line);
        }

        [Fact]
        public void SetTypeParameter_WithoutPlan_StillSaysItIsATypeEdit()
        {
            var line = CardPreview.Line(Call("set_type_parameter",
                "{\"category\":\"Doors\",\"type_name\":\"M_Single 0915\",\"param\":\"Fire Rating\",\"value\":\"60\"}"), null);
            Assert.StartsWith("Edits the TYPE M_Single 0915 (Doors): Fire Rating = \"60\"", line);
            Assert.Contains("every Doors of the type changes, not only the selected ones", line);
        }

        // ── execute_revit_batch (A-07 came through here) ────────────────────

        private static string TypeStepJson(string type, string value = "60") =>
            "{\"tool\":\"set_type_parameter\",\"args\":{\"category\":\"Doors\",\"type_name\":\"" + type
            + "\",\"param\":\"Fire Rating\",\"value\":\"" + value + "\"}}";

        private static Dictionary<string, object> TypePlan(string type, int instances) => new Dictionary<string, object>
        {
            ["ok"] = true, ["dry_run"] = true, ["type_name"] = type, ["parameter"] = "Fire Rating",
            ["current"] = "30", ["new_value"] = "60", ["instances_affected"] = instances,
        };

        [Fact]
        public void Batch_OfTypeEdits_IsOneRow_WithTheTypeCountAndTheWholeModelReach()
        {
            var call = Call("execute_revit_batch", "{\"steps\":[" + TypeStepJson("D1") + "," + TypeStepJson("D2") + "," + TypeStepJson("D3") + "]}");
            var plan = new Dictionary<string, object>
            {
                ["ok"] = true, ["dry_run"] = true,
                ["steps"] = new List<object> { TypePlan("D1", 80), TypePlan("D2", 40), TypePlan("D3", 12) },
            };
            var line = CardPreview.Line(call, plan);
            Assert.Equal("Edits the TYPE: Fire Rating = \"60\" on 3 Doors types · used by 132 Doors in the whole model", line);
            Assert.DoesNotContain("Applying changes to the model", line);
            AssertNoStrayMetric(line);
        }

        [Fact]
        public void Batch_WithoutAPlan_StillSaysTypeEditFromTheArguments()
        {
            var call = Call("execute_revit_batch", "{\"steps\":[" + TypeStepJson("D1") + "," + TypeStepJson("D2") + "]}");
            var line = CardPreview.Line(call, null);
            Assert.StartsWith("Edits the TYPE: Fire Rating = \"60\" on 2 Doors types", line);
            Assert.Contains("every Doors of the type changes, not only the selected ones", line);
        }

        [Fact]
        public void Batch_MixedSteps_GivesOneRowPerKind_RepeatsCollapsed()
        {
            var call = Call("execute_revit_batch", "{\"steps\":["
                + TypeStepJson("D1") + ","
                + "{\"tool\":\"create_wall\",\"args\":{\"level\":\"Aras 01\"}},"
                + "{\"tool\":\"create_wall\",\"args\":{\"level\":\"Aras 01\"}},"
                + "{\"tool\":\"delete_elements\",\"args\":{\"element_ids\":[1,2]}}]}");
            var plan = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["steps"] = new List<object>
                {
                    TypePlan("D1", 80),
                    new Dictionary<string, object> { ["ok"] = true, ["skipped"] = true, ["tool"] = "create_wall" },
                    new Dictionary<string, object> { ["ok"] = true, ["skipped"] = true, ["tool"] = "create_wall" },
                    new Dictionary<string, object> { ["ok"] = true, ["skipped"] = true, ["tool"] = "delete_elements" },
                },
            };
            var rows = CardPreview.Rows(new List<PendingToolCall> { call }, new List<string> { CardPreview.Line(call, plan) });
            Assert.Equal(3, rows.Count);
            Assert.StartsWith("Edits the TYPE D1 (Doors): Fire Rating = \"60\" · used by 80 Doors in the whole model", rows[0]);
            Assert.Equal(ToolLabels.Label("create_wall", J("{\"level\":\"Aras 01\"}")) + " × 2", rows[1]);
            Assert.Equal("Delete 2 elements", rows[2]);
            rows.ForEach(AssertNoStrayMetric);
        }

        [Fact]
        public void Batch_StepsThatFilter_UseTheirOwnPlanRows()
        {
            var call = Call("execute_revit_batch", "{\"steps\":[{\"tool\":\"set_parameter_by_filter\",\"args\":"
                + SetArgs + "}]}");
            var plan = new Dictionary<string, object> { ["ok"] = true, ["steps"] = new List<object> { Plan(46, 46, ("", "semak")) } };
            var line = CardPreview.Line(call, plan);
            Assert.StartsWith("Set Comments to \"semak\" on 46 Doors", line);
            Assert.EndsWith("(46 of 46)", line);
        }

        [Fact]
        public void Batch_OneStepFailsToPreview_ItsRowFallsBack_OthersStay()
        {
            var call = Call("execute_revit_batch", "{\"steps\":[" + TypeStepJson("D1") + "," + TypeStepJson("Nope") + "]}");
            var plan = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["steps"] = new List<object>
                {
                    TypePlan("D1", 80),
                    new Dictionary<string, object> { ["ok"] = false, ["error"] = "no Doors type named 'Nope'", ["candidates"] = new List<object> { "A", "B" } },
                },
            };
            var rows = CardPreview.Rows(new List<PendingToolCall> { call }, new List<string> { CardPreview.Line(call, plan) });
            Assert.Equal(2, rows.Count);
            Assert.StartsWith("Edits the TYPE D1 (Doors)", rows[0]);
            Assert.Contains("could not preview: no Doors type named 'Nope'", rows[1]);
            Assert.DoesNotContain("did you mean", string.Join("|", rows));
            rows.ForEach(AssertNoStrayMetric);
        }

        [Fact]
        public void Batch_WholePreviewFails_RowsComeFromArguments_PlusOneReasonLine()
        {
            var call = Call("execute_revit_batch", "{\"steps\":[" + TypeStepJson("D1") + "]}");
            var plan = new Dictionary<string, object> { ["ok"] = false, ["error"] = "execute_revit_batch: no active document" };
            var rows = CardPreview.Rows(new List<PendingToolCall> { call }, new List<string> { CardPreview.Line(call, plan) });
            Assert.StartsWith("Edits the TYPE D1 (Doors)", rows[0]);
            Assert.Equal("could not preview: execute_revit_batch: no active document", rows[1]);
        }

        [Fact]
        public void Batch_WithNoUsableSteps_KeepsTheOldLabel()
        {
            Assert.Equal(ToolLabels.Label("execute_revit_batch", J("{}")), CardPreview.Line(Call("execute_revit_batch", "{}"), null));
            Assert.Equal(ToolLabels.Label("execute_revit_batch", J("{\"steps\":[]}")), CardPreview.Line(Call("execute_revit_batch", "{\"steps\":[]}"), null));
        }

        // ── which batch steps may be dry-run (the batch preview must never write) ──

        [Fact]
        public void StepDryRunArgs_OnlyForToolsWithADryRun_AndNeverWithAReference()
        {
            var type = CardPreview.StepDryRunArgs(J(TypeStepJson("D1")));
            Assert.NotNull(type);
            Assert.True(type.Value.GetProperty("dry_run").GetBoolean());
            Assert.Equal("D1", type.Value.GetProperty("type_name").GetString());

            Assert.Null(CardPreview.StepDryRunArgs(J("{\"tool\":\"create_wall\",\"args\":{\"level\":\"A\"}}")));          // no dry run: would really execute
            Assert.Null(CardPreview.StepDryRunArgs(J("{\"tool\":\"delete_elements\",\"args\":{\"element_ids\":[1]}}")));
            Assert.Null(CardPreview.StepDryRunArgs(J("{\"tool\":\"place_door\",\"args\":{\"host_wall_id\":\"$0.created_id\"}}")));
            Assert.Null(CardPreview.StepDryRunArgs(J("{\"tool\":\"set_type_parameter\",\"args\":{\"category\":\"Doors\",\"type_name\":\"$0.type\",\"param\":\"P\",\"value\":\"1\"}}")));  // refers to an earlier step
            Assert.Null(CardPreview.StepDryRunArgs(J("{\"tool\":\"set_type_parameter\"}")));                               // no args
            Assert.Null(CardPreview.StepDryRunArgs(default));
        }

        [Fact]
        public void WantsDryRun_ForPreviewableTools_AndBatchesThatHaveAPreviewableStep()
        {
            Assert.True(CardPreview.WantsDryRun(Call("set_type_parameter", "{}")));
            Assert.True(CardPreview.WantsDryRun(Call("set_parameter_by_filter", "{}")));
            Assert.True(CardPreview.WantsDryRun(Call("execute_revit_batch", "{\"steps\":[" + TypeStepJson("D1") + "]}")));
            Assert.False(CardPreview.WantsDryRun(Call("execute_revit_batch", "{\"steps\":[{\"tool\":\"create_wall\",\"args\":{}}]}")));
            Assert.False(CardPreview.WantsDryRun(Call("execute_revit_batch", "{}")));
            Assert.False(CardPreview.WantsDryRun(Call("delete_elements", "{\"element_ids\":[1]}")));
            Assert.False(CardPreview.WantsDryRun(null));
        }

        // ── tools without a dry run: rows come from their own arguments ─────

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
        public void Never_BlockedOnACardThatCanStillBeAllowed()
        {
            // "Blocked" is not used by any row today; every refusal is "could not preview".
            var rows = new[]
            {
                CardPreview.Line(Call("set_parameter_by_filter", SetArgs), new Dictionary<string, object> { ["ok"] = false, ["error"] = "x" }),
                CardPreview.Line(Call("swap_type_by_filter", "{\"category\":\"Doors\",\"type_name\":\"T\"}"), new Dictionary<string, object> { ["ok"] = false }),
                CardPreview.Line(Call("renumber_elements", "{\"category\":\"Doors\"}"), new Dictionary<string, object> { ["ok"] = false }),
            };
            foreach (var r in rows) Assert.DoesNotContain("Blocked", r);
        }

        // ── renumber_elements (A-12) ────────────────────────────────────────

        private const string RenumberArgs = "{\"category\":\"Doors\",\"parameter\":\"Mark\",\"start\":1,\"level\":\"PTBR_Aras 01\"}";

        private static Dictionary<string, object> RenumberPlanResult(int matched, int would, string[] first, string last,
            int replaces = 0, int unchanged = 0, int noRoom = 0, int notWritable = 0) =>
            new Dictionary<string, object>
            {
                ["ok"] = true, ["dry_run"] = true, ["matched"] = matched, ["would_set"] = would,
                ["unchanged"] = unchanged, ["replaces_existing"] = replaces,
                ["skipped_no_room"] = noRoom, ["not_writable"] = notWritable,
                ["first"] = first.Select(t => (object)new Dictionary<string, object> { ["from"] = "", ["to"] = t }).ToList(),
                ["last_mark"] = last, ["nothing"] = would == 0,
            };

        [Fact]
        public void Renumber_FromThePlan_ShowsCountOrderingAndTheFirstNewMarks()
        {
            var line = CardPreview.Line(Call("renumber_elements", RenumberArgs),
                RenumberPlanResult(46, 46, new[] { "1", "2", "3", "4", "5", "6", "7", "8" }, "46", replaces: 40));
            Assert.Equal("Renumber Mark on 46 Doors on PTBR_Aras 01 · in element-id order, not by room or position"
                       + " · new marks 1, 2, 3 … 46 · replaces 40 existing Mark values (46 of 46)", line);
            AssertNoStrayMetric(line);
        }

        [Fact]
        public void Renumber_ByRoom_SaysSo_AndShowsTheRoomMarks()
        {
            var line = CardPreview.Line(Call("renumber_elements",
                "{\"category\":\"Doors\",\"parameter\":\"Mark\",\"by_room\":true,\"level\":\"PTBR_Aras 01\"}"),
                RenumberPlanResult(46, 44, new[] { "R01", "R02", "R02A", "R03", "R04", "R05", "R06", "R07" }, "R30", noRoom: 2));
            Assert.Contains("numbered by room (A, B… when a room has several)", line);
            Assert.DoesNotContain("element-id order", line);
            Assert.Contains("new marks R01, R02, R02A … R30", line);
            Assert.Contains("2 with no room, skipped", line);
            Assert.EndsWith("(44 of 46)", line);
        }

        [Fact]
        public void Renumber_ByPosition_SaysReadingOrder()
        {
            var line = CardPreview.Line(Call("renumber_elements",
                "{\"category\":\"Doors\",\"order\":\"position\",\"start\":1}"),
                RenumberPlanResult(10, 10, new[] { "1", "2", "3", "4", "5", "6", "7", "8" }, "10"));
            Assert.Contains("in reading order, top-left to bottom-right", line);
        }

        [Fact]
        public void Renumber_FewElements_ListsAllTheirMarks()
        {
            var line = CardPreview.Line(Call("renumber_elements", RenumberArgs),
                RenumberPlanResult(3, 3, new[] { "D-1", "D-2", "D-3" }, "D-3"));
            Assert.Contains("new marks D-1, D-2, D-3 ", line);
            Assert.DoesNotContain("…", line);
        }

        [Fact]
        public void Renumber_NothingToDo_SaysSo()
        {
            var none = CardPreview.Line(Call("renumber_elements", RenumberArgs), RenumberPlanResult(0, 0, new string[0], null));
            Assert.StartsWith("Nothing will change: no Doors on PTBR_Aras 01 matched", none);

            var locked = CardPreview.Line(Call("renumber_elements", RenumberArgs),
                RenumberPlanResult(5, 0, new string[0], null, notWritable: 5));
            Assert.StartsWith("Nothing will change: 5 Doors on PTBR_Aras 01 matched, none can be renumbered", locked);
            Assert.Contains("5 without Mark, skipped", locked);
        }

        [Fact]
        public void Renumber_WithoutAPlan_StillSaysTheOrdering_AndNoCountItDoesNotKnow()
        {
            var line = CardPreview.Line(Call("renumber_elements",
                "{\"category\":\"Doors\",\"parameter\":\"Mark\",\"start\":101,\"prefix\":\"D-\",\"level\":\"Aras 01\"}"), null);
            Assert.StartsWith("Renumber Mark on every Doors on Aras 01 · in element-id order, not by room or position", line);
            Assert.Contains("from D-101", line);
            Assert.Contains("replaces the current Mark values", line);
            Assert.DoesNotContain("(", line);
        }

        [Fact]
        public void Renumber_FailedPreview_FallsBackToTheLabel()
        {
            var call = Call("renumber_elements", "{\"category\":\"Nope\"}");
            var line = CardPreview.Line(call, new Dictionary<string, object> { ["ok"] = false, ["error"] = "category 'Nope' not recognised" });
            Assert.StartsWith(ToolLabels.Label("renumber_elements", call.Args), line);
            Assert.Contains("could not preview: category 'Nope' not recognised", line);
        }

        [Fact]
        public void Renumber_InsideABatch_UsesItsOwnPlanRow()
        {
            var call = Call("execute_revit_batch", "{\"steps\":[{\"tool\":\"renumber_elements\",\"args\":" + RenumberArgs + "}]}");
            var plan = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["steps"] = new List<object> { RenumberPlanResult(46, 46, new[] { "1", "2", "3", "4", "5" }, "46") },
            };
            Assert.StartsWith("Renumber Mark on 46 Doors on PTBR_Aras 01", CardPreview.Line(call, plan));
            Assert.True(CardPreview.WantsDryRun(call));
            Assert.True(CardPreview.StepDryRunArgs(J("{\"tool\":\"renumber_elements\",\"args\":" + RenumberArgs + "}")).HasValue);
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
        public void MissingArguments_NeverThrow_NeverEmpty()
        {
            foreach (var tool in new[] { "set_parameter_by_filter", "swap_type_by_filter", "set_type_parameter",
                                          "set_parameter_bulk", "delete_elements", "renumber_elements",
                                          "create_ceiling", "create_floor", "create_sheets_batch", "execute_revit_batch" })
            {
                var c = new PendingToolCall { Tool = tool };      // Args = default(JsonElement)
                AssertNoStrayMetric(CardPreview.Line(c, null));
                AssertNoStrayMetric(CardPreview.Line(c, new Dictionary<string, object>()));
                AssertNoStrayMetric(CardPreview.Line(c, new Dictionary<string, object> { ["ok"] = false, ["error"] = "boom (x)" }));
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
        public void Rows_SplitMultiLineEntries_IntoOneRowEach()
        {
            var pending = new List<PendingToolCall> { Call("execute_revit_batch", "{}"), Call("delete_elements", "{\"element_ids\":[1]}") };
            var rows = CardPreview.Rows(pending, new List<string> { "row one\nrow two\n\n  row three ", "Delete 1 element" });
            Assert.Equal(new[] { "row one", "row two", "row three", "Delete 1 element" }, rows);
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
        public void DryRunCopy_OfABatch_FlagsTheBatchItself_AndKeepsItsSteps()
        {
            var call = Call("execute_revit_batch", "{\"steps\":[" + TypeStepJson("D1") + "]}");
            var copy = CardPreview.DryRunCopy(call);
            Assert.True(copy.Args.GetProperty("dry_run").GetBoolean());
            Assert.Equal(1, copy.Args.GetProperty("steps").GetArrayLength());
            Assert.False(copy.Mutate);
        }

        [Fact]
        public void HasDryRun_IsExactlyTheFourToolsThatWriteNothingInDryMode()
        {
            Assert.True(CardPreview.HasDryRun("set_parameter_by_filter"));
            Assert.True(CardPreview.HasDryRun("swap_type_by_filter"));
            Assert.True(CardPreview.HasDryRun("set_type_parameter"));
            Assert.True(CardPreview.HasDryRun("renumber_elements"));
            Assert.False(CardPreview.HasDryRun("delete_elements"));
            Assert.False(CardPreview.HasDryRun("create_ceiling"));
            Assert.False(CardPreview.HasDryRun("execute_revit_batch"));
        }
    }
}
