using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-434: the multi-valid-code picker. A row whose rule has several valid codes (Kod_Jenis /
    /// Sistem / Sub_Sistem) shows a dropdown of them; the person chooses; Fix all never writes it;
    /// each element is written and re-checked on its own and the row flips only when the rule re-passes.
    /// </summary>
    public class JkrPickerTests
    {
        private static readonly string[] Three = { "PKk", "PKg", "PKb" };
        private const string Rule = "Kod_Jenis_jkr_stt value valid";

        private static IssueVm Wall(string id, long element, IEnumerable<string> options = null, string suggested = "", string reason = "")
        {
            var i = new IssueVm
            {
                Id = id, CheckId = "c" + id, RawRule = Rule, Title = "Kod_Jenis", Category = "Walls", RevitCategory = "Walls",
                RevitElementId = element, Actual = "(none)", Required = "one of PKk, PKg, PKb",
                Element = new ElementRef { Id = element.ToString(), Name = "Basic Wall" },
                Spec = new SpecRef { Doc = "" },
            };
            i.SetPick(options ?? Three, suggested, reason, requiresUserChoice: true);
            return i;
        }

        // ── the DTO fields reach the row; older backends send none ──

        [Fact]
        public void The_backend_fields_map_onto_the_issue()
        {
            var i = Wall("1", 10, suggested: "PKg", reason: "Concrete wall");
            Assert.True(i.IsPick);
            Assert.Equal(Three, i.ValidOptions);
            Assert.Equal("PKg", i.AiSuggestedCode);
            Assert.Equal("Concrete wall", i.AiSuggestionReason);
        }

        [Fact]
        public void An_older_backend_sends_nothing_and_the_row_is_not_a_pick()
        {
            var i = new IssueVm { Id = "1" };
            i.SetPick(null, null, null, false);
            Assert.False(i.IsPick);
            Assert.Empty(i.ValidOptions);
            Assert.Equal("", i.AiSuggestedCode);
        }

        [Fact]
        public void A_suggestion_outside_the_options_is_dropped_and_options_are_deduplicated()
        {
            var i = new IssueVm { Id = "1" };
            i.SetPick(new[] { "PKk", "", "PKk", "PKg" }, "ZZ", "x", true);
            Assert.Equal(new[] { "PKk", "PKg" }, i.ValidOptions);
            Assert.Equal("", i.AiSuggestedCode);
        }

        [Fact]
        public void Requires_user_choice_without_options_is_not_a_pick()
        {
            var i = new IssueVm { Id = "1" };
            i.SetPick(null, "PKk", "", true);
            Assert.False(i.IsPick);
        }

        // ── Fix all never writes a pick row ──

        [Fact]
        public void A_pick_row_is_never_bulk_fixable_even_with_a_fix_attached()
        {
            var i = Wall("1", 10);
            i.AutoFixable = true; i.FixAction = "set_parameter"; i.FixValue = "PKk";
            Assert.False(i.BulkFixable);
            var plain = new IssueVm { AutoFixable = true, FixAction = "set_parameter" };
            Assert.True(plain.BulkFixable);
        }

        [Fact]
        public void SetPick_clears_AutoFixable()
        {
            var i = new IssueVm { Id = "1", AutoFixable = true, FixAction = "set_parameter" };
            i.SetPick(Three, "PKk", "", true);
            Assert.False(i.AutoFixable);
        }

        // ── the picker control's model: all options, the AI badge, the reason ──

        private static JkrFixerRow Picker(string suggested, string reason = "", string lang = "en") =>
            new JkrFixerRow("1", Rule, "Walls", FixerAiState.NotAsked, options: Three, lang: lang,
                            isPick: true, pickCode: suggested, pickReason: reason);

        [Fact]
        public void The_picker_shows_all_three_options_preselected_to_the_ai_code_with_a_badge_and_the_reason()
        {
            var p = Picker("PKg", "Concrete wall");
            Assert.True(p.IsPick);
            Assert.Equal(Three, p.Options);
            Assert.True(p.HasOptions);
            Assert.Equal("PKg", p.ManualValue);
            Assert.True(p.ShowAiBadge);
            Assert.Equal("AI suggested", p.AiBadge);
            Assert.Equal("Concrete wall", p.PickTooltip);
            Assert.True(p.CanWriteManual);
        }

        [Fact]
        public void Without_an_ai_code_the_first_option_is_preselected_with_no_badge()
        {
            var p = Picker("");
            Assert.Equal("PKk", p.ManualValue);
            Assert.False(p.ShowAiBadge);
            Assert.True(p.CanAsk);
        }

        [Fact]
        public void The_badge_follows_the_chosen_value_and_the_badge_text_follows_the_language()
        {
            var p = Picker("PKg", lang: "ms");
            Assert.Equal("Dicadangkan AI", p.AiBadge);
            p.ManualValue = "PKb";
            Assert.False(p.ShowAiBadge);
            p.ManualValue = "PKg";
            Assert.True(p.ShowAiBadge);
        }

        [Fact]
        public void A_pick_row_has_no_accept_button_and_only_a_listed_value_can_be_written()
        {
            var p = Picker("PKg");
            Assert.False(p.CanAccept);
            p.ManualValue = "PKx";
            Assert.False(p.CanWriteManual);
            p.ManualValue = "pkb";
            Assert.True(p.CanWriteManual);
            Assert.Equal("PKb", p.ManualPlan.Value);
            Assert.Equal("set_parameter", p.ManualPlan.Action);
            Assert.Equal("Kod_Jenis_jkr_stt", p.ManualPlan.ParameterName);
        }

        // ── the pane card ──

        [Fact]
        public void The_results_adapter_hands_the_options_and_ai_code_to_the_card()
        {
            var item = ResultsAdapter.FromIssue(Wall("1", 10, suggested: "PKg", reason: "Concrete wall"), "en");
            Assert.True(item.AiFix);
            Assert.True(item.Pick);
            Assert.Equal(Three, item.Options);
            Assert.Equal("PKg", item.PickCode);
            Assert.Equal("Concrete wall", item.AiReason.In("en"));
            Assert.Null(item.Prop);                              // no value is proposed as a ready fix
            Assert.Equal(FixKind.None, item.Fix);
        }

        // ── the confirm dialog: pick bucket, one picker per category + parameter ──

        private static JkrConfirmRow PickRow(string id, long element, string category = "Walls", string suggested = "PKg") =>
            JkrConfirmRow.FromIssue(Wall(id, element, suggested: suggested, reason: "r"));

        [Fact]
        public void Pick_rows_go_in_their_own_bucket_beside_ready_and_ai()
        {
            var ready = new JkrConfirmRow("Walls", "Ready", "old", "new");
            var ai = new JkrConfirmRow("Walls", "AI", "old", "new", isAiProposal: true);
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, new[] { ready, ai, PickRow("1", 10) }, 3);
            Assert.Single(m.ReadyRows);
            Assert.Single(m.AiRows);
            Assert.Single(m.PickRows);
            Assert.True(m.HasPick);
            Assert.Equal("Needs your choice", m.PickHeader);
            Assert.Equal("Write 3 changes", m.ConfirmLabel);
        }

        [Fact]
        public void Three_walls_share_one_picker_with_all_three_options_but_each_row_is_its_own_write()
        {
            var rows = new[] { PickRow("1", 10), PickRow("2", 11), PickRow("3", 12) };
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, rows, 3);
            var g = Assert.Single(m.PickGroups);
            Assert.Equal(Three, g.Options);
            Assert.Equal(3, g.Rows.Count);
            Assert.Equal("PKg", g.Selected);
            Assert.True(g.ShowAiBadge);
            Assert.Equal("Write 3 changes", m.ConfirmLabel);
            g.Selected = "PKb";
            Assert.All(rows, r => Assert.Equal("PKb", m.SelectedFor(r)));
            Assert.False(g.ShowAiBadge);
        }

        [Fact]
        public void Different_parameters_or_categories_get_their_own_picker()
        {
            var doors = Wall("4", 20); doors.Category = "Doors"; doors.RevitCategory = "Doors";
            var sys = Wall("5", 21); sys.RawRule = "Sistem_jkr_stt value valid";
            var m = new JkrConfirmModel(JkrConfirmKind.Fix,
                new[] { PickRow("1", 10), JkrConfirmRow.FromIssue(doors), JkrConfirmRow.FromIssue(sys) }, 3);
            Assert.Equal(3, m.PickGroups.Count);
        }

        [Fact]
        public void The_dialog_can_confirm_only_while_every_picker_holds_a_listed_value()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, new[] { PickRow("1", 10) }, 1);
            Assert.True(m.CanConfirm);
            m.PickGroups[0].Selected = "nope";
            Assert.False(m.CanConfirm);
            Assert.False(m.TryConfirm());
            m.PickGroups[0].Selected = "PKk";
            Assert.True(m.TryConfirm());
        }

        // ── the request keeps the picker's inputs ──

        [Fact]
        public void The_trimmed_autofix_request_keeps_valid_options_and_requires_user_choice()
        {
            var req = new JkrAutoFixRequest();
            req.Issues.Add(new JkrAutoFixIssue { CheckId = "c1", Rule = Rule, ValidOptions = Three.ToList(), RequiresUserChoice = true });
            req.Issues.Add(new JkrAutoFixIssue { CheckId = "c2", Rule = Rule });
            var t = JkrAutoFixPayload.Trim(req);
            Assert.Equal(Three, t.Issues[0].ValidOptions);
            Assert.True(t.Issues[0].RequiresUserChoice);
            Assert.Null(t.Issues[1].ValidOptions);                  // an ordinary issue sends neither field
            Assert.False(t.Issues[1].RequiresUserChoice);
        }

        // ── a pick row flips only after the re-check ──

        private sealed class Row : IRecheckRow
        {
            public string Id { get; set; }
            public long RevitElementId { get; set; }
            public RecheckState RecheckState { get; set; } = RecheckState.Open;
            public bool FixVerified { get; set; }
        }

        [Fact]
        public void Each_element_is_checked_on_its_own_and_only_the_one_that_passes_flips()
        {
            var a = new Row { Id = "a", RevitElementId = 10 };
            var b = new Row { Id = "b", RevitElementId = 11 };
            var c = new Row { Id = "c", RevitElementId = 12 };
            // b still fails after its write: the fresh scan still lists it.
            var o = JkrRecheck.Reconcile(new[] { a, b, c }, new long[] { 10, 11, 12 }, new[] { new Row { Id = "b", RevitElementId = 11 } },
                                         true, new[] { a, b, c });
            Assert.Equal(new[] { "a", "c" }, o.Verified.Select(r => r.Id).OrderBy(x => x));
            Assert.Equal(new[] { "b" }, o.StillFailing.Select(r => r.Id));
        }

        [Fact]
        public void Writing_a_pick_row_does_not_resolve_it_by_itself()
        {
            // The write only loads the chosen value; the row is still Open until the re-check says so.
            var i = Wall("1", 10);
            i.UseManualValue(JkrManualFix.Plan(i.RawRule, i.RevitCategory, "PKb", i.ValidOptions));
            Assert.Equal("PKb", i.FixValue);
            Assert.Equal("set_parameter", i.FixAction);
            Assert.Equal(IssueStatus.Open, i.Status);
            Assert.False(i.AutoFixable);
            Assert.False(i.BulkFixable);
        }
    }
}
