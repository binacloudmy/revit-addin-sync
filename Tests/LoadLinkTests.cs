#nullable enable
// AI-529 (WP-7): the Revit-free half of load_link. Arguments, the per-link decision, the mixed-batch refusal and
// the names. The Revit calls (Load, LoadFrom, Create, the file dialog) cannot run here: they are checked on a
// Revit test box (see the PR body for what is and is not verified).

using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LoadLinkTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static WriteRefusal Refused(string json) => Assert.Throws<WriteRefusal>(() => LoadLinkArgs.Parse(Json(json)));

        [Fact]
        public void Links_alone_are_taken_trimmed_and_without_duplicates()
        {
            var a = LoadLinkArgs.Parse(Json("{\"links\":[\" jkrST24 \",\"JKRST24\",\"jkrME24\",1234]}"));
            Assert.Equal(new[] { "jkrST24", "jkrME24", "1234" }, a.Links);
            Assert.Null(a.Path);
        }

        [Fact]
        public void One_link_with_a_path_and_a_path_alone_are_both_valid()
        {
            var one = LoadLinkArgs.Parse(Json("{\"links\":[\"jkrST24\"],\"path\":\"C:\\\\Models\\\\jkrST24.rvt\"}"));
            Assert.Equal("C:\\Models\\jkrST24.rvt", one.Path);
            var alone = LoadLinkArgs.Parse(Json("{\"path\":\"\\\\\\\\server\\\\share\\\\ME.RVT\"}"));
            Assert.Empty(alone.Links);
            Assert.Equal("\\\\server\\share\\ME.RVT", alone.Path);
        }

        [Fact]
        public void Null_members_count_as_absent()
        {
            Assert.Equal(new[] { "ST" }, LoadLinkArgs.Parse(Json("{\"links\":[\"ST\"],\"path\":null}")).Links);
            Assert.Equal("D:\\x.rvt", LoadLinkArgs.Parse(Json("{\"links\":null,\"path\":\"D:\\\\x.rvt\"}")).Path);
        }

        [Theory]
        [InlineData("{}", "Give links")]
        [InlineData("{\"links\":[]}", "Give links")]
        [InlineData("{\"path\":\"  \"}", "Give links")]
        [InlineData("[]", "JSON object")]
        [InlineData("{\"links\":\"ST\"}", "list of link names")]
        [InlineData("{\"links\":[\"\"]}", "blank")]
        [InlineData("{\"links\":[{\"a\":1}]}", "blank or non-text")]
        [InlineData("{\"links\":[\"ST\"],\"force\":true}", "Unknown argument 'force'")]
        [InlineData("{\"path\":7}", "path must be text")]
        [InlineData("{\"path\":\"jkrST24.rvt\"}", "full Windows path")]
        [InlineData("{\"path\":\"/Users/a/ST.rvt\"}", "full Windows path")]
        [InlineData("{\"path\":\"C:\\\\Models\\\\ST.rfa\"}", ".rvt")]
        [InlineData("{\"links\":[\"ST\",\"ME\"],\"path\":\"C:\\\\Models\\\\ST.rvt\"}", "at most one link")]
        public void Every_problem_is_invalid_args_with_its_reason(string json, string reason)
        {
            var r = Refused(json);
            Assert.Equal(ErrorCodes.InvalidArgs, r.Code);
            Assert.Contains(reason, r.Message);
        }

        [Fact]
        public void More_than_the_limit_of_links_is_refused_and_exactly_the_limit_is_not()
        {
            string Many(int n) => "{\"links\":[" + string.Join(",", Enumerable.Range(1, n).Select(i => "\"L" + i + "\"")) + "]}";
            Assert.Equal(10, LoadLinkArgs.MaxLinks);
            Assert.Equal(10, LoadLinkArgs.Parse(Json(Many(10))).Links.Count);
            var r = Refused(Many(11));
            Assert.Equal(ErrorCodes.InvalidArgs, r.Code);
            Assert.Contains("At most 10 links per call", r.Message);
        }

        [Theory]
        [InlineData("C:\\a.rvt", true)]
        [InlineData("c:/a.rvt", true)]
        [InlineData("\\\\server\\share\\a.rvt", true)]
        [InlineData("RSN://server/a.rvt", false)]
        [InlineData("BIM 360://Project/a.rvt", false)]
        [InlineData("a.rvt", false)]
        [InlineData("/mnt/a.rvt", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void A_local_path_is_a_drive_or_unc_path(string? path, bool local) => Assert.Equal(local, LinkLoadRules.IsLocalPath(path));

        [Theory]
        // loaded: nothing to do, whatever else is known
        [InlineData(LinkStatusKind.Loaded, true, null, LinkAction.None)]
        [InlineData(LinkStatusKind.Loaded, false, "C:\\a.rvt", LinkAction.None)]
        // unloaded with its file in place: reload from the stored path; the file picker never opens
        [InlineData(LinkStatusKind.Unloaded, true, null, LinkAction.Reload)]
        // the stored file is missing, or Revit could not find it: ask the drafter for the file
        [InlineData(LinkStatusKind.Unloaded, false, null, LinkAction.Pick)]
        [InlineData(LinkStatusKind.NotFound, true, null, LinkAction.Pick)]
        [InlineData(LinkStatusKind.NotFound, false, null, LinkAction.Pick)]
        // a path in the call wins over the stored one and over the picker
        [InlineData(LinkStatusKind.Unloaded, true, "D:\\new\\a.rvt", LinkAction.LoadFrom)]
        [InlineData(LinkStatusKind.NotFound, false, "D:\\new\\a.rvt", LinkAction.LoadFrom)]
        // states load_link does not touch
        [InlineData(LinkStatusKind.ClosedWorkset, true, null, LinkAction.Refuse)]
        [InlineData(LinkStatusKind.Other, true, null, LinkAction.Refuse)]
        public void Each_link_gets_the_action_its_state_calls_for(LinkStatusKind status, bool storedFileExists, string? path, LinkAction expected)
        {
            var d = LinkLoadRules.Decide(status, storedFileExists, path, "jkrST24");
            Assert.Equal(expected, d.Action);
            if (expected == LinkAction.Refuse)
            {
                Assert.Contains("'jkrST24'", d.Reason);
                Assert.Contains("Nothing was loaded", d.Reason);
            }
            else Assert.Null(d.Reason);
        }

        [Fact]
        public void A_closed_workset_is_refused_with_the_way_out()
        {
            Assert.Contains("open that workset", LinkLoadRules.Decide(LinkStatusKind.ClosedWorkset, true, null, "ST").Reason);
        }

        [Fact]
        public void Load_link_never_shares_a_batch_with_another_call()
        {
            Assert.Null(LinkLoadRules.MixedBatchRefusal(new[] { "load_link" }));
            Assert.Null(LinkLoadRules.MixedBatchRefusal(new[] { "set_parameters", "run_revit_code" }));
            Assert.Null(LinkLoadRules.MixedBatchRefusal(new string[0]));
            foreach (var other in new[] { "set_parameters", "run_revit_code", "inspect", "load_link" })
            {
                var why = LinkLoadRules.MixedBatchRefusal(new[] { "load_link", other });
                Assert.NotNull(why);
                Assert.Contains("load_link runs on its own", why);
                Assert.Contains("Nothing was changed", why);
            }
        }

        [Theory]
        [InlineData("LinkNotFound", "could not find the file")]
        [InlineData("LinkNotOpenable", "could not read the file")]
        [InlineData("SameModelAsHost", "host model itself")]
        [InlineData("LinkOpenAsHost", "host model itself")]
        [InlineData("ExternalServerMissing", "add-in that serves")]
        [InlineData("LinkExists", "already exists")]
        [InlineData("LinkNotLoadedOtherError", "Revit refused (LinkNotLoadedOtherError)")]
        public void A_refused_load_is_explained_in_plain_words(string revitResult, string words) => Assert.Contains(words, LinkLoadRules.Explain(revitResult));

        [Fact]
        public void The_tool_is_a_write_tool_the_add_in_runs_and_follows_run_revit_code_in_the_contract_order()
        {
            Assert.Equal("write", ToolNames.KindOf(ToolNames.LoadLink));
            Assert.Contains(ToolNames.LoadLink, ToolNames.AddIn);
            Assert.Equal(ToolNames.RunRevitCode, ToolNames.All[ToolNames.All.ToList().IndexOf(ToolNames.LoadLink) - 1]);
            Assert.Equal(13, ToolNames.All.Count);   // AI-530: bind_parameters follows load_link; AI-605: read_dwg_plan follows it
        }

        [Fact]
        public void The_write_label_and_undo_group_name_say_what_the_tool_does()
        {
            var plan = new WritePlan { Tool = ToolNames.LoadLink, CanonicalArgs = "{\"links\":[\"ST\"]}" };
            Assert.Equal("Load link", WriteLabels.ForPlan(plan));
            Assert.Equal("BINA: Load link", WriteLabels.GroupName(new[] { plan }));
        }
    }
}
