using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-535: Da.1 passes only a REAL JKR code (the "jkrAR" prefix alone is not one) and the Copilot fix never invents a code.</summary>
    public class Da1RealCodeTests
    {
        private static long _next = 5000;

        private static SpecType T(string category, string typeName, string family = "", int instances = 3, string codeParam = null)
        {
            var t = new SpecType
            {
                TypeId = ++_next, Category = category, TypeName = typeName, FamilyName = family, InstanceCount = instances,
                InstanceIds = Enumerable.Range(0, instances).Select(_ => ++_next).ToList(),
            };
            if (codeParam != null) t.Texts["Kod_Komponen_jkr_stt"] = codeParam;
            return t;
        }

        private static List<SpecResult> Run(params SpecType[] types)
        {
            var m = new SpecModel();
            m.Types.AddRange(types);
            return SpecChecks.Rules["Da.1"](m.Normalize()).ToList();
        }

        private static List<SpecResult> Fails(List<SpecResult> r) => r.Where(x => x.Status == "fail").ToList();

        // ── the three names Copilot invented on the tester's run 2 (AI-535 Da.1): all must FAIL ──

        [Theory]
        [InlineData("Stairs", "jkrAR_Cast-In-Place-Stair_180_280")]
        [InlineData("Curtain Panels", "jkrAR_System-Panel_100")]
        [InlineData("Pipes", "jkrAR_UPVC_Schedule-40_100")]
        public void An_invented_jkrAR_name_with_no_code_fails_with_a_clear_remark(string category, string invented)
        {
            var f = Assert.Single(Fails(Run(T(category, invented))));
            Assert.Contains("prefix alone is not a code", f.Note);
            Assert.Equal(SpecCoverage.Full, f.Coverage);
        }

        [Fact]
        public void The_old_prefix_only_pass_is_gone_and_the_check_fails_when_only_invented_names_are_left()
        {
            var r = Run(T("Stairs", "jkrAR_Cast-In-Place-Stair_180_280"), T("Curtain Panels", "jkrAR_System-Panel_100"), T("Pipes", "jkrAR_UPVC_Schedule-40_100"));
            Assert.Equal(3, Fails(r).Count);
            Assert.DoesNotContain(r, x => x.Status == "pass");
        }

        // ── real names pass (the test model's style included) ──

        [Theory]
        [InlineData("Stairs", "(TGc002) 180mm max riser 280mm", "")]
        [InlineData("Doors", "(TKh281a) 600 x 1800", "")]
        [InlineData("Doors", "(PTr001a) 900 x 2100", "")]
        [InlineData("Doors", "(PT2p600a) 900 x 2325 a", "")]
        [InlineData("Walls", "jkrAR_wll-b_(bb02)-3 Batu Bata", "")]
        [InlineData("Floors", "jkrAR_flr_LTd_kt1_150_jh_ex_ (LTd200a)-3 @LT01", "")]
        [InlineData("Windows", "1800 X 500 mm @TK11a", "jkrAR18_wdw_TKt_Q12b1_ex_(TKt001)-3 @TK01")]
        public void A_real_code_passes(string category, string typeName, string family)
        {
            var r = Run(T(category, typeName, family));
            Assert.Empty(Fails(r));
            Assert.NotEmpty(r.Where(x => x.Status == "pass"));
        }

        [Fact]
        public void A_code_shaped_token_whose_type_code_prefix_is_not_in_the_list_fails_naming_the_code()
        {
            var f = Assert.Single(Fails(Run(T("Stairs", "(TGx002) 180mm max riser 280mm"))));
            Assert.Contains("'(TGx002)' is not in the JKR code list", f.Note);
        }

        [Fact]
        public void A_family_name_with_only_the_prefix_does_not_rescue_the_type()
        {
            Assert.Single(Fails(Run(T("Walls", "Basic Wall 200", "jkrAR_Basic_Wall"))));
        }

        // ── the list itself ──

        [Theory]
        [InlineData("TGc002", true)]
        [InlineData("TGc002a", true)]
        [InlineData("LBk201a", true)]
        [InlineData("PTr001", true)]
        [InlineData("Co100", true)]
        [InlineData("TGx002", false)]
        [InlineData("TGc02", false)]
        [InlineData("tgc002", false)]
        [InlineData("ZZq123", false)]
        [InlineData("jkrAR", false)]
        [InlineData("", false)]
        public void Component_codes(string code, bool real) => Assert.Equal(real, JkrCodeList.IsComponentCode(code));

        // ── no invention: the fix derives the code in the add-in or asks ──

        [Fact]
        public void A_type_whose_own_parameter_holds_a_real_code_gets_the_derived_name_and_nothing_else_is_invented()
        {
            var own = T("Stairs", "Cast-In-Place Stair 180 280", codeParam: "TGc002");
            var none = T("Stairs", "Monolithic Stair 150 250");
            var bad = T("Pipes", "UPVC Schedule 40 100", codeParam: "jkrAR_x");
            var fails = Fails(Run(own, none, bad));
            var a = fails.Single(f => f.ElementName.Contains("Cast-In-Place"));
            Assert.Equal("(TGc002) Cast-In-Place Stair 180 280", a.RenameTo);
            Assert.Equal("Cast-In-Place Stair 180 280", a.RenameFrom);
            Assert.Contains("Kod_Komponen_jkr_stt", a.RenameSource);
            Assert.Equal("", a.CodeHint);
            var b = fails.Single(f => f.ElementName.Contains("Monolithic"));
            Assert.Equal("", b.RenameTo);
            Assert.Contains("TGc", b.CodeHint);
            var c = fails.Single(f => f.ElementName.Contains("UPVC"));
            Assert.Equal("", c.RenameTo);   // a parameter value that is not a real code is not used
        }

        [Fact]
        public void The_hint_offers_only_codes_already_in_the_model_and_is_the_same_on_every_run()
        {
            var good = T("Stairs", "(TGc002) 180mm max riser 280mm", instances: 40);
            var other = T("Stairs", "(TGc001) 150mm max riser 250mm", instances: 5);
            var bad = T("Stairs", "Stair 3");
            string Hint() => Fails(Run(good, other, bad)).Single().CodeHint;
            var h = Hint();
            Assert.Equal(h, Hint());
            Assert.Contains("codes already in use for Stairs in this model: (TGc002), (TGc001)", h);
            Assert.DoesNotContain("TGc003", h);
        }

        [Fact]
        public void A_derived_name_that_clashes_with_an_existing_type_is_dropped_so_the_person_is_asked()
        {
            var a = T("Stairs", "Stair A", "Fam", codeParam: "TGc002");
            var existing = T("Stairs", "(TGc002) Stair A", "Fam");
            var f = Fails(Run(a, existing)).Single();
            Assert.Equal("", f.RenameTo);
        }

        private static CopilotPromptFacts AskFacts(bool derived)
        {
            var ids = new List<long> { 1, 2 };
            return new CopilotPromptFacts
            {
                CheckId = "Da.1", Category = "Stairs", Required = "(XXxnnn)", Actual = "Stair A",
                ElementIds = ids, ElementLabels = ids.ToDictionary(i => i, i => "Stairs · Stair " + i),
                Renames = ids.ToDictionary(i => i, i => derived
                    ? new CopilotRename { Old = "Stair " + i, New = "(TGc002) Stair " + i, Source = "the type's own Kod_Komponen_jkr_stt parameter holds the JKR code TGc002" }
                    : new CopilotRename { Old = "Stair " + i, Ask = true, Source = "type-code prefix TGc; codes already in use for Stairs in this model: (TGc002)" }),
            };
        }

        [Fact]
        public void The_prompt_lists_the_derived_name_exactly_and_tells_copilot_never_to_pick_a_code()
        {
            var p = Assert.Single(JkrCopilotPrompt.Build(AskFacts(true)));
            Assert.Contains("- 1 — Stairs · \"Stair 1\" -> \"(TGc002) Stair 1\"", p);
            Assert.Contains("NEVER choose, guess, derive, shorten or invent a code", p);
            Assert.Contains("never write a \"jkrAR\" prefix name", p);
            Assert.DoesNotContain("\"Stair 1\" -> ASK", p);
        }

        [Fact]
        public void An_ask_line_carries_the_hint_and_the_prompt_asks_once_for_all_of_them()
        {
            var p = Assert.Single(JkrCopilotPrompt.Build(AskFacts(false)));
            Assert.Contains("- 1 — Stairs · \"Stair 1\" -> ASK: type-code prefix TGc; codes already in use for Stairs in this model: (TGc002)", p);
            Assert.Contains("call ask_user ONCE for all ASK lines together", p);
            Assert.Equal(p, Assert.Single(JkrCopilotPrompt.Build(AskFacts(false))));   // same prompt every run
            var c = JkrCopilotConfirm.Build(AskFacts(false));
            Assert.Contains("never picks one", c.Body);
        }
    }
}
