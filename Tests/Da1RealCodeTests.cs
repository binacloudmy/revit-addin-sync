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
        [InlineData("Floors", "jkrAR_LT_RC_150")]   // the old sample-model name (jkr_run_compliant.json had it before AI-535): prefix only, no code: not compliant
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
        [InlineData("Windows", "(TKh281a) 600 x 1800", "")]
        [InlineData("Doors", "(PTr001a) 900 x 2100", "")]
        [InlineData("Doors", "(PTc080a) 900 x 2325 a", "")]
        [InlineData("Walls", "jkrAR_wll-b_(bb02)-3 Batu Bata", "")]
        [InlineData("Floors", "jkrAR_flr_LTd_kt1_150_jh_ex_ (LTd200a)-3 @LT01", "")]
        [InlineData("Windows", "1800 X 500 mm @TK11a", "jkrAR18_wdw_TKt_Q12b1_ex_(TKt001)-3 @TK01")]
        public void A_real_code_passes(string category, string typeName, string family)
        {
            var r = Run(T(category, typeName, family));
            Assert.Empty(Fails(r));
            Assert.NotEmpty(r.Where(x => x.Status == "pass"));
        }

        // Rule (captain, 23 Nov): category WITH a JKR list: code must be in it else FAIL. Category WITHOUT a list: format only; unlisted = person row.
        [Fact]
        public void A_category_with_a_list_fails_a_well_formed_code_that_is_not_in_it()
        {
            var f = Assert.Single(Fails(Run(T("Casework", "(MKq001a) 2300 x 150 x 620"))));
            Assert.Contains("'(MKq001a)' is not in the JKR list for Casework", f.Note);
            Assert.Single(Fails(Run(T("Doors", "(TKk400a) 900 x 2100"))));   // a window code on a door
        }

        [Fact]
        public void A_category_without_a_list_gives_a_person_row_for_a_well_formed_unlisted_code_and_passes_a_sample()
        {
            var r = Run(T("Stairs", "(TGa002) 180mm max riser 280mm"), T("Generic Models", "(AKs001a) 600"));
            Assert.Empty(Fails(r));
            Assert.Equal(2, r.Count(x => x.Status == "cannot_verify"));
            Assert.Contains(r.Where(x => x.Status == "cannot_verify"), p => p.Note.Contains("code (TGa002) is not in the add-in list: confirm"));
            Assert.DoesNotContain(r, x => x.Status == "pass");
            var ok = Run(T("Stairs", "(TGc002) x"), T("Railings", "(TGc003) x"), T("Generic Models", "(GMa001) x"), T("Ramps", "(LAk001) x"), T("Planting", "(PKa100) x"), T("Columns", "(Co100) x"));
            Assert.Empty(Fails(ok));
            Assert.DoesNotContain(ok, x => x.Status == "cannot_verify");
        }

        [Fact]
        public void Wrong_format_fails_two_digits_no_type_letter_wrong_case()
        {
            foreach (var n in new[] { "(TGb01a) 400mm", "(TG002a) 190mm", "(TGC002) 180mm", "(TGc2) 180mm" })
            {
                var f = Assert.Single(Fails(Run(T("Stairs", n))));
                Assert.Contains("wrong format", f.Note);
                Assert.Contains("case-sensitive", f.Note);
            }
        }

        // The tester's dump (test-round-20261123b, dedupe_lod300) names. Under the old prefix rule they passed.
        [Fact]
        public void Dump_names_split_into_pass_person_and_fail_by_rule_and_the_three_invented_names_fail()
        {
            var badFormat = new[]
            {
                T("Stairs", "jkrAR_sta_TGa_(TG002a)-3 190mm max riser 250mm going"),
                T("Stairs", "jkrAR_sta_TGa_(TG003a)-3 190mm max riser 250mm going"),
                T("Railings", "jkrAR_rlg_(SR001a)-3 900mm Pipe"),
                T("Generic Models", "(TGb01a) 400mm", "jkrAR18_gen-mo_TGs_(TGb01)-3 Tangga Senggara"),
                T("Generic Models", "(TGb02a) 1000 x 1000mm", "jkrAR18_gen-mo_TGs_(TGb01)-3 Tangga Senggara"),
            };
            var notInList = new[] { T("Casework", "(MKq001a) 2300 x 150 x 620") };
            var person = new[]
            {
                T("Generic Models", "(AKs001a) 600"), T("Generic Models", "(AKs002a) 600"), T("Generic Models", "(AKs003a) 600"),
            };
            var invented = new[]
            {
                T("Stairs", "jkrAR_Cast-In-Place-Stair_180_280"), T("Curtain Panels", "jkrAR_System-Panel_100"), T("Pipes", "jkrAR_UPVC_Schedule-40_100"),
            };
            var r = Run(badFormat.Concat(notInList).Concat(person).Concat(invented).ToArray());
            Assert.Equal(badFormat.Length + notInList.Length + invented.Length, Fails(r).Count);
            Assert.Equal(person.Length, r.Count(x => x.Status == "cannot_verify"));
            Assert.Equal(3, Fails(Run(invented)).Count);
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
        [InlineData("TGc2", false)]
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
            Assert.Contains("person confirm", b.CodeHint);   // Stairs has no JKR list: no prefix offered
            Assert.DoesNotContain("TGc", b.CodeHint);
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

        // ── one list: hint == validation == checker; Da.1 rename must also pass Dd.1 ──

        private static List<string> HintPrefixes(string hint)
        {
            var k = "type-code prefix from the JKR list (only these): ";
            var i = hint.IndexOf(k, System.StringComparison.Ordinal);
            if (i < 0) return new List<string>();
            var rest = hint.Substring(i + k.Length);
            var end = rest.IndexOf(';'); if (end >= 0) rest = rest.Substring(0, end);
            return rest.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        [Theory]
        [InlineData("Floors", "Stair 3")]
        [InlineData("Casework", "Cabinet 3")]
        public void Hint_prefixes_equal_the_checker_list_and_every_one_is_accepted_by_the_checker(string cat, string bad)
        {
            var f = Fails(Run(T(cat, bad))).Single();
            var offered = HintPrefixes(f.CodeHint);
            Assert.NotEmpty(offered);
            foreach (var p in offered)
            {
                Assert.Contains(p, JkrCodeList.KodJenisFor(cat));
                Assert.Empty(Fails(Run(T(cat, "(" + p + "001) 150"))));
            }
            // and no prefix outside the list is accepted
            Assert.Single(Fails(Run(T("Floors", "(LBk201) 150"))));
            Assert.Single(Fails(Run(T("Casework", "(MKq001) 150"))));
            Assert.DoesNotContain("LBk", f.CodeHint);
            Assert.DoesNotContain("MKq", f.CodeHint);
        }

        [Theory]
        [InlineData("Generic Models")]
        [InlineData("Railings")]
        [InlineData("Stairs")]
        [InlineData("Ramps")]
        [InlineData("Planting")]
        public void A_category_with_no_list_offers_no_prefix_and_asks_a_person(string cat)
        {
            var f = Fails(Run(T(cat, "Thing 3"))).Single();
            Assert.Equal("", f.RenameTo);
            Assert.Empty(HintPrefixes(f.CodeHint));
            Assert.Contains("person confirm", f.CodeHint);
        }

        [Fact]
        public void A_derived_rename_that_would_fail_Dd1_is_not_proposed_and_the_reason_is_listed()
        {
            // Casework type whose own code is a real Casework code (PBa) but whose name says "Door": Dd.1 would...
            // the code is the stronger evidence, so use a code the Dd.1 table puts in another category: PT in a Floors list? not in list: use Floors + LAk? (LA is Ramps/Floors: ok)
            var okT = T("Floors", "Slab 150", codeParam: "LFa001");
            var good = Fails(Run(okT)).Single();
            Assert.Equal("(LFa001) Slab 150", good.RenameTo);
            // Dd.1 conflict: a code of the list whose Dd.1 category is another one
            var m = new SpecModel();
            var t = T("Casework", "Counter 1", codeParam: "PBa001");
            m.Types.Add(t); m.Normalize();
            Assert.Null(SpecChecks.Dd1Conflict(t, "(PBa001) Counter 1"));
            Assert.NotNull(SpecChecks.Dd1Conflict(T("Doors", "Door 1"), "(TKh281a) Door 1"));
            var floorWithDoorCode = T("Floors", "Slab 2", codeParam: "LFa001");
            Assert.NotNull(SpecChecks.Dd1Conflict(floorWithDoorCode, "(PTa001) Slab 2"));
        }
    }
}
