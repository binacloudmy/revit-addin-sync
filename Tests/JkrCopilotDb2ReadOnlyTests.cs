using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>Db.2 "Fix via Copilot": a read-only parameter is skipped up front and reported (never planned, never retried), the writable
    /// JKR material parameters (*_jkr_stm) are named by the add-in, and a multi-part fix goes on after a not_allowed part.</summary>
    [Collection("ForcedConfirmStatic")]
    public class JkrCopilotDb2ReadOnlyTests
    {
        private static long _id = 91000;

        private static SpecType Door(params SpecMaterialParam[] ps)
        {
            var t = new SpecType
            {
                TypeId = ++_id, Category = "Doors", TypeName = "D1", FamilyName = "F", InstanceCount = 2,
                InstanceIds = new List<long> { ++_id, ++_id },
                MaterialParams = ps.ToList(), MaterialParamsRead = true,
            };
            return t;
        }

        private static SpecMaterialParam P(string name, bool onType, bool readOnly, string value) =>
            new SpecMaterialParam { Name = name, OnType = onType, ReadOnly = readOnly, Value = value };

        private static List<SpecResult> Db2(SpecType t)
        {
            var m = new SpecModel();
            m.Types.Add(t);
            return SpecChecks.Run(m.Normalize()).Where(r => r.CheckId == "Db.2" && r.Status == "fail").ToList();
        }

        // ── the evaluator names the parameters ──

        [Fact]
        public void Door_with_empty_Kaca_jkr_stm_gets_the_JKR_param_named_and_a_filled_one_is_left_out()
        {
            var t = Door(P("Kaca_jkr_stm", true, false, ""), P("Bingkai_jkr_stm", true, false, "jkrAR_dor-b_(ab)-2 Aluminium"), P("Panel_jkr_stm", true, false, "Plywood"));
            t.Materials.Add("Plywood");   // a non-JKR material: the row fails
            var r = Assert.Single(Db2(t));
            Assert.Equal(new[] { "Kaca_jkr_stm", "Panel_jkr_stm" }, r.ParamsToFill.Select(x => x.Name).ToArray());
            Assert.All(r.ParamsToFill, x => Assert.True(x.OnType));
            Assert.Empty(r.ParamsSkipped);
        }

        [Fact]
        public void A_read_only_param_is_skipped_with_its_reason_and_the_row_still_fails()
        {
            var t = Door(P("Material", false, true, ""), P("Kaca_jkr_stm", true, false, ""));
            var r = Assert.Single(Db2(t));                       // not weakened: still a failure
            Assert.Equal("Kaca_jkr_stm", Assert.Single(r.ParamsToFill).Name);
            var skip = Assert.Single(r.ParamsSkipped);
            Assert.Equal("Material", skip.Name);
            Assert.Equal("read-only", skip.Reason);
        }

        [Fact]
        public void Only_read_only_params_leave_nothing_to_fill_and_the_row_still_fails()
        {
            var r = Assert.Single(Db2(Door(P("Material", false, true, ""))));
            Assert.Empty(r.ParamsToFill);
            Assert.Single(r.ParamsSkipped);
        }

        [Fact]
        public void No_param_list_read_gives_no_plan_the_fix_falls_back_to_Material()
        {
            var t = Door(); t.MaterialParamsRead = false;
            var r = Assert.Single(Db2(t));
            Assert.Empty(r.ParamsToFill);
            Assert.Empty(r.ParamsSkipped);
        }

        // ── the prompt ──

        private static CopilotPromptFacts Facts(params (long id, CopilotMaterialPlan plan)[] rows) => new CopilotPromptFacts
        {
            CheckId = "Db.2", Category = "Doors", ElementIds = rows.Select(r => r.id).ToList(),
            ElementLabels = rows.ToDictionary(r => r.id, r => "Doors · D" + r.id),
            Materials = rows.Where(r => r.plan != null).ToDictionary(r => r.id, r => r.plan),
        };

        private static CopilotMaterialPlan Fill(params string[] names) => new CopilotMaterialPlan { Fill = names.Select(n => new SpecParamTarget { Name = n, OnType = true }).ToList() };
        private static CopilotMaterialPlan ReadOnly(string name) => new CopilotMaterialPlan { Skipped = new List<SpecParamTarget> { new SpecParamTarget { Name = name, Reason = "read-only" } } };

        [Fact]
        public void The_line_names_the_JKR_params_to_set()
        {
            var p = Assert.Single(JkrCopilotPrompt.Build(Facts((11, Fill("Kaca_jkr_stm", "Panel_jkr_stm")))));
            Assert.Contains("- 11 — Doors · D11 — set: Kaca_jkr_stm (type parameter), Panel_jkr_stm (type parameter)", p);
            Assert.Contains("never use a parameter it does not name", p);
        }

        [Fact]
        public void A_row_with_no_plan_keeps_the_Material_wording()
        {
            var p = Assert.Single(JkrCopilotPrompt.Build(Facts((11, null))));
            Assert.DoesNotContain("set:", p.Split('\n').First(l => l.StartsWith("- 11")));
            Assert.Contains("param \"Material\"", p);
        }

        [Fact]
        public void Elements_with_only_read_only_params_are_left_out_of_the_prompt_and_reported()
        {
            var f = Facts((11, Fill("Kaca_jkr_stm")), (12, ReadOnly("Material")), (13, ReadOnly("Stringer Material")));
            Assert.Equal(new long[] { 11 }, JkrCopilotPrompt.TargetIds(f).ToArray());
            var p = Assert.Single(JkrCopilotPrompt.Build(f));
            Assert.DoesNotContain("- 12", p);
            Assert.DoesNotContain("- 13", p);
            var note = JkrCopilotPrompt.CantWriteNote(f);
            Assert.Contains("2 elements can't be written here", note);
            Assert.Contains("Material (read-only)", note);
            Assert.Contains("Stringer Material (read-only)", note);
            Assert.Contains("keep failing the check", note);
        }

        [Fact]
        public void A_mixed_element_lists_the_writable_param_and_names_the_read_only_one()
        {
            var plan = new CopilotMaterialPlan { Fill = Fill("Kaca_jkr_stm").Fill, Skipped = ReadOnly("Material").Skipped };
            var p = Assert.Single(JkrCopilotPrompt.Build(Facts((11, plan))));
            Assert.Contains("set: Kaca_jkr_stm (type parameter); can't be written here: Material (read-only)", p);
            Assert.Equal("", JkrCopilotPrompt.CantWriteNote(Facts((11, plan))));   // something is written: no receipt of its own
        }

        [Fact]
        public void All_read_only_means_no_prompt_and_the_confirmed_write_is_not_built()
        {
            var f = Facts((12, ReadOnly("Material")));
            Assert.Empty(JkrCopilotPrompt.Build(f));
            Assert.Null(JkrCopilotConfirmedWrite.ForPart(f, 0));
        }

        [Fact]
        public void The_confirmed_write_params_are_unchanged()
        {
            var dto = JkrCopilotConfirmedWrite.ForPart(Facts((11, Fill("Kaca_jkr_stm"))), 0);
            Assert.Equal(new[] { "Material" }, dto.Params.ToArray());   // no skip-set change: a write on another param is asked as before
        }

        [Fact]
        public void The_confirm_dialog_lists_the_params_and_counts_only_writable_elements()
        {
            var c = JkrCopilotConfirm.Build(Facts((11, Fill("Kaca_jkr_stm")), (12, ReadOnly("Material"))));
            Assert.Equal("Let Copilot change 1 element for check Db.2?", c.Heading);
            Assert.Contains("set: Kaca_jkr_stm (type parameter)", c.Details);
        }

        [Fact]
        public void Merge_keeps_each_rows_plan()
        {
            var m = JkrCopilotPrompt.MergeFacts(new[] { Facts((11, Fill("Kaca_jkr_stm"))), Facts((12, ReadOnly("Material"))) });
            Assert.NotNull(JkrCopilotPrompt.MaterialPlanOf(m, 11));
            Assert.True(JkrCopilotPrompt.MaterialPlanOf(m, 12).Unwritable);
        }

        // ── the part loop ──

        private sealed class Session : ICopilotSession, ICopilotToolError, ICopilotToolErrorCode
        {
            public Func<int, (CopilotCardState card, string code, string msg)> End;   // per part (0-based)
            public readonly List<string> Sent = new List<string>();
            private int _n;
            private (CopilotCardState card, string code, string msg) Cur => Sent.Count == 0 ? (CopilotCardState.None, null, null) : End(Sent.Count - 1);
            public Task<string> OpenPaneAsync() => Task.FromResult<string>(null);
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsPaneVisible => true;
            public bool IsSending => Sent.Count > 0 && _n++ < 2;
            public CopilotCardState Cards => Sent.Count > 0 && _n >= 2 ? Cur.card : CopilotCardState.None;
            public string LastToolError => _n >= 2 ? Cur.msg : null;
            public string LastToolErrorCode => _n >= 2 ? Cur.code : null;
            public void Send(string text) { Sent.Add(text); _n = 0; }
        }

        private static readonly (CopilotCardState, string, string) Wrote = (CopilotCardState.Approved, null, null);
        private static readonly (CopilotCardState, string, string) NotAllowed = (CopilotCardState.None, "not_allowed", "'Material' is read-only on every matched element (Revit computes or locks it).");

        private static (JkrCopilotFixLauncher l, List<string> toasts, List<int> rechecks) Make(Session s, string[] parts, string skip = null)
        {
            var toasts = new List<string>();
            var rechecks = new List<int>();
            var l = new JkrCopilotFixLauncher(s, _ => parts, toasts.Add, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(5), settle: TimeSpan.FromMilliseconds(500),
                afterWrite: _ => { rechecks.Add(1); return Task.CompletedTask; }, skipNote: _ => skip);
            return (l, toasts, rechecks);
        }

        private static readonly CopilotFixRequest Req = new CopilotFixRequest("i1", "Db.2", new long[] { 1, 2 }, "T");

        [Fact]
        public async Task A_not_allowed_part_does_not_stop_the_later_parts()
        {
            // the Tester's run: part 1 wrote, part 2 was refused (read-only 'Material'), parts 3-4 must still go out
            var s = new Session { End = i => i == 1 ? NotAllowed : Wrote };
            var (l, toasts, rechecks) = Make(s, new[] { "P1", "P2", "P3", "P4" });
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "P1", "P2", "P3", "P4" }, s.Sent.ToArray());
            Assert.Single(rechecks);                                            // re-checked once; the skipped part's elements keep failing there
            var t = Assert.Single(toasts);
            Assert.Contains("Part 2 can't be written here", t);
            Assert.Contains("read-only", t);
            Assert.Contains("keep failing the check", t);
            Assert.DoesNotContain("not needed", t);
        }

        [Fact]
        public async Task A_not_allowed_first_part_still_sends_the_rest_and_several_skips_are_listed()
        {
            var s = new Session { End = i => i == 0 || i == 2 ? NotAllowed : Wrote };
            var (l, toasts, rechecks) = Make(s, new[] { "P1", "P2", "P3" });
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(3, s.Sent.Count);
            Assert.Contains(toasts, t => t.Contains("Parts 1, 3 can't be written here"));
            Assert.Single(rechecks);
        }

        [Fact]
        public async Task Every_part_not_allowed_is_NoWrite_with_no_recheck_and_says_so()
        {
            var s = new Session { End = _ => NotAllowed };
            var (l, toasts, rechecks) = Make(s, new[] { "P1", "P2" });
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await l.LaunchAsync(Req));
            Assert.Equal(2, s.Sent.Count);
            Assert.Empty(rechecks);
            Assert.Contains(toasts, t => t.StartsWith("Every part can't be written here"));
        }

        [Fact]
        public async Task A_real_error_still_stops_the_pack()
        {
            var bad = (CopilotCardState.None, "invalid_args", "Value 'x' for 'Material': not a material.");
            var s = new Session { End = i => i == 0 ? bad : Wrote };
            var (l, toasts, rechecks) = Make(s, new[] { "P1", "P2", "P3" });
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "P1" }, s.Sent.ToArray());                     // parts 2-3 not sent
            Assert.Contains(toasts, t => t.Contains("Parts 2-3 not sent") && t.Contains("not a material"));
            Assert.Empty(rechecks);
        }

        [Fact]
        public async Task A_part_with_no_write_and_no_error_still_stops_as_before()
        {
            var s = new Session { End = i => i == 0 ? Wrote : (CopilotCardState.None, null, null) };
            var (l, toasts, _) = Make(s, new[] { "P1", "P2", "P3" });
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(2, s.Sent.Count);
            Assert.Contains(toasts, t => t.Contains("not needed"));
        }

        [Fact]
        public async Task The_skip_receipt_is_shown_with_the_result_and_alone_when_nothing_is_left_to_send()
        {
            const string skip = "2 elements can't be written here, so no change was tried: Material (read-only). They keep failing the check.";
            var s = new Session { End = _ => Wrote };
            var (l, toasts, _) = Make(s, new[] { "P1" }, skip);
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Contains(skip, toasts);

            var s2 = new Session { End = _ => Wrote };
            var (l2, toasts2, _) = Make(s2, new string[0], skip);
            Assert.Equal(CopilotLaunchOutcome.NoPrompt, await l2.LaunchAsync(Req));
            Assert.Equal(new[] { skip }, toasts2.ToArray());
            Assert.Empty(s2.Sent);
        }
    }
}
