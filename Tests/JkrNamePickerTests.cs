using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-485 (add-in half; backend is bina-ai PR #399). A failing "Naming convention" check on a
    /// multi-subtype category (Wall b/k/d, Door k/g/b, Window b/k) arrives with no fix_action,
    /// requires_user_choice=true and valid_options = whole candidate names. It goes through the
    /// AI-434 picker, with no option preselected (not by the AI, not by position), is never written by
    /// Fix all, and is written only as the name the person chose.
    /// </summary>
    public class JkrNamePickerTests
    {
        private static readonly string[] WallNames =
            { "jkrAR_wll-b Concrete 200", "jkrAR_wll-k Concrete 200", "jkrAR_wll-d Concrete 200" };
        private static readonly string[] DoorNames =
            { "jkrAR_dor-k Timber 900", "jkrAR_dor-g Timber 900", "jkrAR_dor-b Timber 900" };
        private static readonly string[] WindowNames =
            { "jkrAR_win-b Alu 1200", "jkrAR_win-k Alu 1200" };

        /// <summary>The backend's response contract (PR #399 body), one element per multi-subtype category,
        /// plus a Floor whose name has one subtype and so still carries a fix.</summary>
        private static string Json(IEnumerable<string> walls, string aiCode = null)
        {
            string Check(long id, string cat, string type, IEnumerable<string> opts) => JsonConvert.SerializeObject(new
            {
                rule = "Naming convention", status = "fail", element_id = id, category = cat, type_name = type,
                fix_action = (object)null, requires_user_choice = true, confidence = "ambiguous",
                valid_options = opts, fix_suggestion = "Pick one of: " + string.Join("; ", opts),
                ai_suggested_code = aiCode,
            });
            var floor = JsonConvert.SerializeObject(new
            {
                rule = "Naming convention", status = "fail", element_id = 40, category = "Floors", type_name = "Floor 1",
                fix_action = new { action = "rename_type", value = "jkrAR_flr-s Slab 150", old_value = "Floor 1", target = "type", priority = 1 },
                fix_value = "jkrAR_flr-s Slab 150", confidence = "high",
                valid_options = new string[0], requires_user_choice = false,
            });
            return "{\"element_issues\":[" + string.Join(",", new[]
            {
                Check(10, "Walls", "Basic Wall: Concrete 200", walls),
                Check(20, "Doors", "Single Door: Timber 900", DoorNames),
                Check(30, "Windows", "Fixed: Alu 1200", WindowNames),
                floor,
            }) + "]}";
        }

        // IssueMapper / ComplianceService are not linked into Tests.csproj (WPF-side deps), so the contract is read
        // through a small mirror of the DTO fields and handed to the row exactly as IssueMapper.FromDto does:
        // RawRule, FixAction, then SetPick(valid_options, ai_suggested_code, reason, requires_user_choice).
        private sealed class Dto
        {
            [JsonProperty("rule")] public string Rule;
            [JsonProperty("element_id")] public long ElementId;
            [JsonProperty("category")] public string Category;
            [JsonProperty("type_name")] public string TypeName;
            [JsonProperty("fix_action")] public FixDto FixAction;
            [JsonProperty("fix_value")] public string FixValue;
            [JsonProperty("valid_options")] public List<string> ValidOptions;
            [JsonProperty("requires_user_choice")] public bool RequiresUserChoice;
            [JsonProperty("ai_suggested_code")] public string AiSuggestedCode;
        }
        private sealed class FixDto { [JsonProperty("action")] public string Action; [JsonProperty("value")] public string Value; }
        private sealed class Resp { [JsonProperty("element_issues")] public List<Dto> ElementIssues; }

        private static List<IssueVm> Map(string json) =>
            JsonConvert.DeserializeObject<Resp>(json).ElementIssues.Select(d =>
            {
                var vm = new IssueVm
                {
                    Id = "i" + d.ElementId, CheckId = "c" + d.ElementId, RawRule = d.Rule, Title = d.Rule, Category = d.Category,
                    RevitCategory = d.Category, RevitElementId = d.ElementId, Actual = d.TypeName ?? "",
                    Element = new ElementRef { Id = d.ElementId.ToString(), Name = d.TypeName },
                    Spec = new SpecRef { Doc = "" },
                    FixAction = d.FixAction?.Action ?? "", FixValue = d.FixValue ?? "",
                    AutoFixable = d.FixAction != null,
                };
                vm.SetPick(d.ValidOptions, d.AiSuggestedCode, "reason", d.RequiresUserChoice);
                return vm;
            }).ToList();
        private static IssueVm Of(List<IssueVm> l, long el) => l.Single(i => i.RevitElementId == el);

        // ── the contract reaches the row, for all three categories ──

        [Theory]
        [InlineData(10, "Walls", 3)]
        [InlineData(20, "Doors", 3)]
        [InlineData(30, "Windows", 2)]
        public void Each_multi_subtype_category_becomes_a_name_pick_with_every_candidate(long el, string category, int count)
        {
            var i = Of(Map(Json(WallNames)), el);
            Assert.Equal(category, i.RevitCategory);
            Assert.True(i.IsPick);
            Assert.True(i.IsNamePick);
            Assert.Equal(count, i.ValidOptions.Count);
            Assert.All(i.ValidOptions, o => Assert.StartsWith("jkrAR_", o));
            Assert.False(i.AutoFixable);
            Assert.Equal("", i.FixAction);
        }

        [Fact]
        public void The_wall_options_keep_the_backend_order_and_are_whole_names()
        {
            Assert.Equal(WallNames, Of(Map(Json(WallNames)), 10).ValidOptions);
        }

        [Fact]
        public void An_unambiguous_category_keeps_its_fix_and_is_not_a_pick()
        {
            var f = Of(Map(Json(WallNames)), 40);
            Assert.False(f.IsPick);
            Assert.False(f.IsNamePick);
            Assert.Equal("rename_type", f.FixAction);
            Assert.Equal("jkrAR_flr-s Slab 150", f.FixValue);
        }

        // ── no guess: nothing preselected ──

        [Fact]
        public void An_ai_code_sent_for_a_name_is_dropped()
        {
            var i = Of(Map(Json(WallNames, aiCode: WallNames[0])), 10);
            Assert.Equal("", i.AiSuggestedCode);
            i.SetPick(i.ValidOptions, WallNames[1], "concrete", true);   // a later AI answer too
            Assert.Equal("", i.AiSuggestedCode);
            Assert.Equal("", i.AiSuggestionReason);
        }

        [Fact]
        public void The_row_picker_starts_empty_and_cannot_be_written_until_a_name_is_chosen()
        {
            var i = Of(Map(Json(WallNames)), 10);
            var p = new JkrFixerRow(i.Id, i.RawRule, i.RevitCategory, FixerAiState.NotAsked, options: i.ValidOptions,
                                    isPick: true, pickCode: WallNames[0], pickReason: "x");
            Assert.True(p.IsNamePick);
            Assert.Equal("", p.ManualValue);
            Assert.False(p.ShowAiBadge);
            Assert.False(p.CanAsk);
            Assert.False(p.CanWriteManual);
            p.ManualValue = WallNames[2];
            Assert.True(p.CanWriteManual);
            Assert.Equal("rename_type", p.ManualPlan.Action);
            Assert.Equal("type", p.ManualPlan.Target);
            Assert.Equal(WallNames[2], p.ManualPlan.Value);
        }

        [Fact]
        public void A_name_outside_the_options_is_refused()
        {
            var plan = JkrManualFix.Plan("Naming convention", "Walls", "jkrAR_wll-b Brick 200", WallNames);
            Assert.False(plan.Ok);
            Assert.True(JkrManualFix.Plan("Naming convention", "Walls", WallNames[1], WallNames).Ok);
        }

        [Fact]
        public void A_code_picker_is_unchanged_and_still_starts_on_its_first_option()
        {
            var p = new JkrFixerRow("1", "Kod_Jenis_jkr_stt value valid", "Walls", FixerAiState.NotAsked,
                                    options: new[] { "PKk", "PKg" }, isPick: true);
            Assert.False(p.IsNamePick);
            Assert.Equal("PKk", p.ManualValue);
        }

        [Fact]
        public void The_confirm_dialog_picker_starts_empty_and_blocks_confirming_until_chosen()
        {
            var i = Of(Map(Json(WallNames)), 10);
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, new[] { JkrConfirmRow.FromIssue(i) }, 1);
            var g = Assert.Single(m.PickGroups);
            Assert.True(g.IsName);
            Assert.Equal("", g.Selected);
            Assert.False(g.ShowAiBadge);
            Assert.False(m.CanConfirm);
            Assert.False(m.TryConfirm());
            g.Selected = WallNames[1];
            Assert.True(m.CanConfirm);
        }

        [Fact]
        public void A_name_the_person_already_picked_on_the_row_carries_into_the_dialog_but_never_as_an_ai_pick()
        {
            var i = Of(Map(Json(WallNames)), 10);
            i.UseManualValue(JkrManualFix.Plan(i.RawRule, i.RevitCategory, WallNames[2], i.ValidOptions));
            var g = new JkrConfirmModel(JkrConfirmKind.Fix, new[] { JkrConfirmRow.FromIssue(i) }, 1).PickGroups[0];
            Assert.Equal(WallNames[2], g.Selected);
            Assert.False(g.ShowAiBadge);
        }

        [Fact]
        public void Walls_of_different_types_do_not_share_a_picker()
        {
            var a = Of(Map(Json(WallNames)), 10);
            var b = Of(Map(Json(new[] { "jkrAR_wll-b Brick 100", "jkrAR_wll-k Brick 100", "jkrAR_wll-d Brick 100" })), 10);
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, new[] { JkrConfirmRow.FromIssue(a), JkrConfirmRow.FromIssue(b) }, 2);
            Assert.Equal(2, m.PickGroups.Count);
        }

        // ── Fix All skips them ──

        [Fact]
        public void Fix_all_never_takes_a_name_pick_but_still_takes_the_unambiguous_fix()
        {
            var all = Map(Json(WallNames));
            var bulk = all.Where(i => i.BulkFixable).ToList();
            Assert.Equal(new long[] { 40 }, bulk.Select(i => i.RevitElementId));
            var needChoice = all.Where(i => i.IsPick).ToList();
            Assert.Equal(3, needChoice.Count);                       // counted as needing a choice, not as fixable
            Assert.All(needChoice, i => Assert.False(i.BulkFixable));
        }

        [Fact]
        public void Writing_a_chosen_name_loads_a_rename_of_exactly_that_name_and_does_not_resolve_the_row()
        {
            var i = Of(Map(Json(WallNames)), 10);
            i.UseManualValue(JkrManualFix.Plan(i.RawRule, i.RevitCategory, WallNames[1], i.ValidOptions));
            Assert.Equal("rename_type", i.FixAction);
            Assert.Equal(WallNames[1], i.FixValue);
            Assert.Equal(IssueStatus.Open, i.Status);
            Assert.False(i.BulkFixable);
        }

        [Fact]
        public void The_pane_card_is_a_pick_with_no_preselected_name_and_no_proposed_value()
        {
            var item = ResultsAdapter.FromIssue(Of(Map(Json(WallNames, aiCode: WallNames[0])), 10), "en");
            Assert.True(item.Pick);
            Assert.Equal(WallNames, item.Options);
            Assert.Equal("", item.PickCode);
            Assert.Null(item.Prop);
            Assert.Equal(FixKind.None, item.Fix);
        }

        [Fact]
        public void An_older_backend_with_no_options_is_not_a_pick_and_not_fixable()
        {
            const string old = "{\"element_issues\":[{\"rule\":\"Naming convention\",\"status\":\"fail\",\"element_id\":10,\"category\":\"Walls\",\"type_name\":\"W\"}]}";
            var i = Map(old).Single();
            Assert.False(i.IsPick);
            Assert.False(i.BulkFixable);
        }
    }
}
