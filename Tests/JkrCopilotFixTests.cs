using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-492 (Revit- and WPF-free half): the code-side list and its eligibility rule, Fix all skips
    /// CopilotFixable rows, BM / EN tooltip, the launcher seam. The IssueVm / detail half is JkrCopilotFixRowTests.</summary>
    [Collection("CopilotLauncher")]
    public class JkrCopilotFixTests
    {
        private static bool Ok(string id, string cat = "Doors", bool person = false, bool fix = false, bool pick = false, int ids = 2) =>
            JkrCopilotFix.Eligible(id, cat, person, fix, pick, ids);

        public static IEnumerable<object[]> Allowed() => JkrCopilotFix.Allowed.Select(id => new object[] { id });

        [Theory]
        [MemberData(nameof(Allowed))]
        public void Every_listed_id_shows_the_button_exactly_when_the_catalog_calls_it_AI(string id)
        {
            Assert.Equal(JkrCopilotFix.IsAiClass(id), Ok(id));
        }

        [Fact]
        public void The_v1_list_is_exactly_the_umbrella_table()
        {
            var want = new[] { "Da.1", "Dd.1", "Db.2", "Db.1", "Dc.1", "A2.2", "A3.1", "A5.2", "C1.6.3", "C4.2.2",
                               "C1.3.1", "C1.4.1", "C1.5.2", "C1.6.1", "C2.1.1", "C3.1.1", "C4.1.3", "C5.1.1", "C1.1.1", "C1.1.2", "C5.1.2" };
            Assert.Equal(want.OrderBy(x => x), JkrCopilotFix.Allowed.OrderBy(x => x));
        }

        [Theory]
        [InlineData("B1.1")] [InlineData("A4.1")] [InlineData("A1.1")] [InlineData("A1.2")] [InlineData("A1.5")] [InlineData("A1.7")] [InlineData("A6.2")]
        [InlineData("A7.3")] [InlineData("B1.2")] [InlineData("C1.2.1")] [InlineData("Da.2")] [InlineData("Dd.2")] [InlineData("E.1")] [InlineData("")] [InlineData("Zz.9")]
        [InlineData("SPEC-Da.1")] [InlineData("da.1")]
        public void Denied_unlisted_and_unknown_ids_never_show_it(string id)
        {
            Assert.False(JkrCopilotFix.IsListed(id));
            Assert.False(Ok(id));
        }

        [Fact]
        public void The_deny_list_wins_even_if_an_id_were_added_to_the_allow_list()
        {
            foreach (var id in JkrCopilotFix.Denied) Assert.DoesNotContain(id, JkrCopilotFix.Allowed);
            Assert.False(JkrCopilotFix.IsListed("A1.3"));
        }

        [Fact]
        public void Human_and_List_class_checks_are_refused_whatever_the_list_says()
        {
            Assert.False(Ok("Da.1", person: true));                 // the row says a person decides
            foreach (var c in SpecCatalog.Checks.Where(c => c.Class != SpecCellClass.Ai))
                Assert.False(Ok(c.Id), c.Id);
        }

        [Fact]
        public void A_row_with_a_Fix_a_picker_or_no_element_id_does_not_show_it()
        {
            Assert.True(Ok("Da.1"));
            Assert.False(Ok("Da.1", fix: true));
            Assert.False(Ok("Da.1", pick: true));
            Assert.False(Ok("Da.1", ids: 0));   // a row restored from the audit snapshot: ids are not persisted
            Assert.True(Ok("Da.1", ids: 1));
        }

        private static readonly string[] ModelLevel = { "C1.3.1", "C1.4.1", "C1.5.2", "C1.6.1", "C3.1.1", "C4.1.3", "C5.1.1" };
        public static IEnumerable<object[]> ModelLevelIds() => ModelLevel.Select(id => new object[] { id });

        [Theory]
        [MemberData(nameof(ModelLevelIds))]
        public void Create_type_rows_show_the_button_with_zero_element_ids_AI498(string id)
        {
            Assert.True(JkrCopilotFix.AllowsNoElementIds(id));
            Assert.True(JkrCopilotFix.Allowed.Contains(id));
            Assert.True(Ok(id, "Views", ids: 0));
            Assert.True(Ok(id, "Views", ids: 3));
            // every other gate still holds with zero ids
            Assert.False(Ok(id, "Views", person: true, ids: 0));
            Assert.False(Ok(id, "Views", fix: true, ids: 0));
            Assert.False(Ok(id, "Views", pick: true, ids: 0));
        }

        [Fact]
        public void Legend_has_no_create_tool_so_no_zero_id_button_but_still_listed_with_ids()
        {
            Assert.False(Ok("C2.1.1", "Views", ids: 0));
            Assert.True(Ok("C2.1.1", "Views", ids: 1));
        }

        [Fact]
        public void The_zero_id_path_is_exactly_the_seven_model_level_ids()
        {
            Assert.Equal(ModelLevel.OrderBy(x => x), JkrCopilotFix.ModelLevel.OrderBy(x => x));
        }

        [Theory]
        [InlineData("C2.1.1")] [InlineData("C1.1.1")] [InlineData("C1.1.2")] [InlineData("C1.6.2")] [InlineData("C5.1.2")]
        [InlineData("Da.1")] [InlineData("Dd.1")] [InlineData("Db.2")] [InlineData("Db.1")] [InlineData("Dc.1")] [InlineData("A2.2")]
        [InlineData("A3.1")] [InlineData("A5.2")] [InlineData("C1.6.3")] [InlineData("C4.2.2")]
        [InlineData("B1.1")] [InlineData("A4.1")] [InlineData("A1.2")] [InlineData("A6.2")] [InlineData("")] [InlineData("Zz.9")]
        public void Every_other_id_still_needs_an_element_id(string id)
        {
            Assert.False(JkrCopilotFix.AllowsNoElementIds(id));
            Assert.False(Ok(id, ids: 0));
        }

        [Fact]
        public void Wall_material_is_never_offered_AI457()
        {
            Assert.False(Ok("Db.2", "Walls"));
            Assert.False(Ok("Db.2", " walls "));
            Assert.True(Ok("Db.2", "Doors"));
        }

        [Fact]
        public void A_row_with_only_the_Copilot_button_counts_it_as_a_control_only_when_it_shows_AI480()
        {
            Assert.False(JkrRowControls.HasFixControl(false, false, false));
            Assert.True(JkrRowControls.HasFixControl(false, false, false, copilotButton: true));
            Assert.False(JkrRowControls.HasAnyControl(false, false, false, false, false));
            Assert.True(JkrRowControls.HasAnyControl(false, false, false, false, false, copilotButton: true));
            // Mark manual: kept for a row with no control, follows the tier map once the button is a control.
            Assert.True(JkrRowControls.CanMarkManual(false, JkrRowControls.HasFixControl(false, false, false)));
            Assert.False(JkrRowControls.CanMarkManual(false, JkrRowControls.HasFixControl(false, false, false, true)));
        }

        [Fact]
        public void Fix_all_skips_CopilotFixable_rows()
        {
            PanelItem P(string id, bool copilot, FixKind fix) => new PanelItem { Id = id, Sec = "A", Fix = fix, CopilotFixable = copilot };
            var core = new PanelCore(new List<PanelItem>
            {
                P("1", false, FixKind.Fix), P("2", true, FixKind.None), P("3", true, FixKind.Fix), P("4", false, FixKind.None),
            }, null, new ManualClock());
            Assert.Equal(new[] { "1" }, core.FixableOpen().Select(x => x.Id));
            Assert.Equal(1, core.FixableChanges());
            core.FixAll();
            Assert.Equal(new[] { "1" }, core.Confirm.Ids);
        }

        [Fact]
        public void The_tooltip_and_label_follow_the_pane_language()
        {
            Assert.Equal("Fix via Copilot", JkrCopilotFix.Label("en"));
            Assert.Contains("new Copilot chat", JkrCopilotFix.Tooltip("en"));
            Assert.Contains("confirm", JkrCopilotFix.Tooltip("en"));
            Assert.Equal("Baiki melalui Copilot", JkrCopilotFix.Label("ms"));
            Assert.Contains("sembang Copilot baharu", JkrCopilotFix.Tooltip("ms"));
            Assert.Contains("sahkan", JkrCopilotFix.Tooltip("ms"));
            var was = PaneLanguage.Current;
            try
            {
                PaneLanguage.Set(PanelStrings.En); Assert.Equal(JkrCopilotFix.Tooltip("en"), JkrCopilotFix.Tooltip());
                PaneLanguage.Set(PanelStrings.Ms); Assert.Equal(JkrCopilotFix.Tooltip("ms"), JkrCopilotFix.Tooltip());
            }
            finally { PaneLanguage.Set(was); }
        }

        private sealed class Spy : ICopilotFixLauncher
        {
            public CopilotFixRequest Got;
            public void Launch(CopilotFixRequest r) => Got = r;
        }

        [Fact]
        public void The_click_does_nothing_without_a_launcher_and_hands_the_request_to_one_when_set()
        {
            var req = new CopilotFixRequest("r1", "Da.1", new long[] { 100, 101 }, "Naming");
            JkrCopilotFix.Launcher = null;
            Assert.False(JkrCopilotFix.Request(req));
            var spy = new Spy();
            JkrCopilotFix.Launcher = spy;
            try
            {
                Assert.True(JkrCopilotFix.Request(req));
                Assert.Same(req, spy.Got);
                Assert.Equal(new long[] { 100, 101 }, spy.Got.ElementIds);
            }
            finally { JkrCopilotFix.Launcher = null; }
        }
    }
}
