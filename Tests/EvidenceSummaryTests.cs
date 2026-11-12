#nullable enable
// AI-269 items 1 and 3: the evidence strip in one line with the names behind Details, one "not
// searched" line for the turn, one Export per table, no glued text at a tool round, no developer
// line in the step trail. Revit-free.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class EvidenceSummaryTests
    {
        private static readonly string[] SixLinks =
        {
            "link: jkrST22_2b_(BEdeA1_14-001)_A1_w-01_(S)_DS-PC_190124 (unloaded)",
            "link: 2017-11-17a_jkrME18-3_(BEdeA1_14-001)_A1_w-01_(K)_ACMV (unloaded)",
            "link: jkrME22-3_(BEdeA1_14-001)_A1_w-01_(K)_CWS (unloaded)",
            "link: jkrME22-3_(BEdeA1_14-001)_A1_w-01_(K)_FP (unloaded)",
            "link: jkrEL22_3b_(BEdeA1_14-p01)_A1_w-01_(S)_LV_171117a (unloaded)",
            "link: jkrME22-3_(BEdeA1_14-001)_A1_w-01_(K)_ACMV (unloaded)",
        };

        private static EvidenceItem Read(int count, string filter = "", int? selected = null, int? visible = null) => new EvidenceItem
        {
            Tool = "find_elements", Count = count, Sources = { "host" }, Categories = { "Doors" }, Filter = filter,
            Selected = selected, VisibleInView = visible, NotSearched = SixLinks.ToList(),
        };

        private static ProgressStep Step(string id, string label, string phase = "executing", StepState state = StepState.Done) =>
            new ProgressStep { StepId = id, Label = label, Phase = phase, State = state };


        [Fact]
        public void TheTurnBudget_IsADeveloperStep_AndNeverTheChipText()
        {
            var steps = new List<ProgressStep>
            {
                Step("p:1", "Thinking", "thinking"),
                Step("call:c1", "Finding elements"),
                Step("budget", "Tools 1/60 · 0.0/2.0 MB · images 0/8"),
            };
            Assert.True(ProgressTrail.IsDevStep(steps[2]));
            Assert.DoesNotContain(ProgressTrail.UserVisible(steps), s => s.StepId == "budget");
            Assert.Equal("Finding elements", ProgressTrail.Preview(steps));           // not the budget, not "+2"
        }

        [Fact]
        public void Preview_OfOnlyThinkingRows_IsEmpty_NotThinkingPlus24()
        {
            var steps = Enumerable.Range(0, 25).Select(i => Step("p:" + i, "Thinking", "thinking")).ToList();
            Assert.Equal("", ProgressTrail.Preview(steps));
        }

        [Fact]
        public void Preview_CountsOnlyTheStepsThatSayWhatRan()
        {
            var steps = new List<ProgressStep>
            {
                Step("p:1", "Thinking", "thinking"),
                Step("call:a", "Reading the model overview"),
                Step("call:b", "Finding elements"),
                Step("budget", "Tools 2/60"),
            };
            Assert.Equal("Finding elements  +1", ProgressTrail.Preview(steps));
        }

        // ---- follow-ups from the after benchmark -----------------------------------------------------------

        private static ResultTable CheckTable(string rule, string cite, string pass, string fail, string notVerified, string why) => new ResultTable
        {
            Kind = ResultTable.CheckKind, Title = "Check jkr 1",
            Columns = { "Rule", "Cite", "Severity", "Pass", "Fail", "Not verified", "Why not verified" },
            Rows = { new List<string> { rule, cite, "warning", pass, fail, notVerified, why } },
        };

        [Fact]
        public void CheckRuleLine_IsShortAndPlain_TheRegexOnlyInTheFullLine()
        {
            var cite = "Uniform Building By-Laws 1984 (as amended), by-law 136 (Provision of compartment walls and compartment floors); IPM guide 'Lukisan Perlindungan Kebakaran Aktif dan Pasif Bomba' p.1 items 6-7";
            var why = "0 of 1458 Walls matched Komponen_Bomba_jkr_sty in [Yes, Ya, 1] or Fire Rating not empty or type name pattern '(?i)(fire|kalis[ _-]?api|rintangan[ _-]?api)'";
            var t = CheckTable("BB-02", cite, "0", "0", "1", why);
            var line = ReceiptTemplates.CheckRuleLine(t, 0, ReceiptLanguage.En);
            Assert.Equal("BB-02 · Uniform Building By-Laws 1984 (as amended), by-law 136… — 0 fail · 0 pass · 1 not verified (see Details)", line);
            Assert.DoesNotContain("(?i)", line);
            Assert.DoesNotContain("[Yes", line);
            Assert.Contains("(?i)", ReceiptTemplates.CheckRuleFullLine(t, 0, ReceiptLanguage.En));       // the full text is kept for Details
            Assert.EndsWith("(lihat Butiran)", ReceiptTemplates.CheckRuleLine(t, 0, ReceiptLanguage.Ms));
        }

        [Fact]
        public void CheckRuleLine_ShortCiteAndShortReason_AreUnchanged()
        {
            var t = CheckTable("L3-14", "JKR LOD 4.4", "40", "0", "3", "link STR unloaded");
            Assert.Equal("L3-14 · JKR LOD 4.4 — 0 fail · 40 pass · 3 not verified (link STR unloaded)", ReceiptTemplates.CheckRuleLine(t, 0, ReceiptLanguage.En));
            Assert.Equal(ReceiptTemplates.CheckRuleLine(t, 0, ReceiptLanguage.En), ReceiptTemplates.CheckRuleFullLine(t, 0, ReceiptLanguage.En));
        }

        private static ToolRequestDto WriteRequest() => new ToolRequestDto
        {
            RequestId = "req_w", Kind = ToolRequestKinds.Write,
            Calls = new List<ToolCallDto> { new ToolCallDto { CallId = "w0", Tool = ToolNames.RunRevitCode, Args = System.Text.Json.JsonSerializer.SerializeToElement(new { }) } },
        };

        [Fact]
        public void Receipt_ACountDeltaCheck_IsNotTheNumberOfChangedElements()
        {
            // Live test (5 Nov): a write that edited one schedule reported "21984/21984 done"; count_delta's
            // `checked` is every element in the document, not what the write changed.
            var ev = new TurnEvidence();
            ev.Add(WriteRequest(), new[]
            {
                new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran.",
                               Verified = new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = 21984 } },
            });
            var f = ev.Receipt;
            Assert.Equal(1, f.Total);
            Assert.Equal(1, f.Done);
            Assert.Equal("1/1 done", ReceiptTemplates.Headline(f, ReceiptLanguage.En));
        }

        [Fact]
        public void Receipt_AnExistsCheckAndTouchedIdsStillCount()
        {
            var ev = new TurnEvidence();
            ev.Add(WriteRequest(), new[]
            {
                new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran.",
                               Verified = new VerifiedDto { Method = VerifyMethods.Exists, Checked = 3 } },
            });
            Assert.Equal(3, ev.Receipt.Total);                                  // exists: the elements it checked are the changed ones
        }

        private static Envelope WriteEnv(string status, string phase = EnvelopePhase.Commit) =>
            new Envelope { Ok = status == EnvelopeStatus.Done, Status = status, Phase = phase, Summary = "x", Count = 1 };

        private static ToolRequestDto RequestOf(string tool) => new ToolRequestDto
        {
            RequestId = "req_" + tool, Kind = ToolRequestKinds.Write,
            Calls = new List<ToolCallDto> { new ToolCallDto { CallId = "w0", Tool = tool, Args = System.Text.Json.JsonSerializer.SerializeToElement(new { }) } },
        };

        private static TurnEvidence TurnOf(params (string Tool, Envelope Env)[] writes)
        {
            var ev = new TurnEvidence();
            foreach (var (tool, env) in writes) ev.Add(RequestOf(tool), new[] { env });
            return ev;
        }

        [Fact]
        public void Caution_AGenuineRetry_FailedBeforeCommitThenDoneSameTool_IsANoteNotAmber()
        {
            var ev = TurnOf((ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Failed, EnvelopePhase.Preview)), (ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Done)));
            Assert.False(ev.Caution);
            Assert.Equal(1, ev.RetriedAttempts);
            Assert.Equal("1 attempt failed; the retry was applied.", ReceiptTemplates.RetryNote(1, ReceiptLanguage.En));
            Assert.Equal("2 attempts failed; the retry was applied.", ReceiptTemplates.RetryNote(2, ReceiptLanguage.En));
        }

        [Fact]
        public void Caution_APartialWriteThenAnUnrelatedDoneWrite_StaysAmber()
        {
            // 30 of 34 done, 4 failed (set_parameters), then a schedule write that is done: the partial is not a retry.
            var ev = TurnOf((ToolNames.SetParameters, WriteEnv(EnvelopeStatus.Partial)), (ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Done)));
            Assert.True(ev.Caution);
            Assert.Equal(0, ev.RetriedAttempts);
        }

        [Fact]
        public void Caution_APartialWriteThenADoneWriteOfTheSameTool_StaysAmber()
        {
            var ev = TurnOf((ToolNames.SetParameters, WriteEnv(EnvelopeStatus.Partial)), (ToolNames.SetParameters, WriteEnv(EnvelopeStatus.Done)));
            Assert.True(ev.Caution);                                          // something may have changed: never "retried"
            Assert.Equal(0, ev.RetriedAttempts);
        }

        [Fact]
        public void Caution_ACommitPhaseFailure_ThenADoneWrite_StaysAmber()
        {
            var ev = TurnOf((ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Failed, EnvelopePhase.Commit)), (ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Done)));
            Assert.True(ev.Caution);
            Assert.Equal(0, ev.RetriedAttempts);
        }

        [Fact]
        public void Caution_ADeclinedWrite_ThenADoneWrite_StaysAmber()
        {
            var ev = TurnOf((ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Rejected, EnvelopePhase.Preview)), (ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Done)));
            Assert.True(ev.Caution);
            Assert.Equal(0, ev.RetriedAttempts);
        }

        [Fact]
        public void Caution_TwoIndependentPlans_AFailureWithNoMatchingLaterDoneWrite_StaysAmber()
        {
            // set_parameters failed before commit; only a run_revit_code write is done afterwards: a different plan.
            var ev = TurnOf((ToolNames.SetParameters, WriteEnv(EnvelopeStatus.Failed, EnvelopePhase.Preview)), (ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Done)));
            Assert.True(ev.Caution);
            Assert.Equal(0, ev.RetriedAttempts);
            // the failure comes last: amber
            var last = TurnOf((ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Done)), (ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Failed, EnvelopePhase.Preview)));
            Assert.True(last.Caution);
        }

        [Fact]
        public void Caution_OnlyDone_OrOnlyFailed()
        {
            Assert.False(TurnOf((ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Done))).Caution);
            var failed = TurnOf((ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Failed)));
            Assert.True(failed.Caution);
            Assert.Equal(0, failed.RetriedAttempts);
        }

        private static Envelope Unchanged() =>
            new Envelope { Ok = true, Status = EnvelopeStatus.Noop, Phase = EnvelopePhase.Commit, Summary = "Ran: sheets. No elements were created or removed.", Count = 1 };

        [Fact]
        public void Caution_CodeThatChangedNothing_StopsToAsk_IsNotAmber()
        {
            // Live test 12 Nov: the sheet-set script printed NOTHING MADE and the model asked the drafter; nothing failed.
            var ev = TurnOf((ToolNames.RunRevitCode, Unchanged()));
            Assert.False(ev.Caution);
            Assert.Equal("0/1 done · 1 unchanged", ReceiptTemplates.Headline(ev.Receipt, ReceiptLanguage.En));   // the receipt still says it
            // the drafter answered and the rerun made the sheets: still no amber
            Assert.False(TurnOf((ToolNames.RunRevitCode, Unchanged()), (ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Done))).Caution);
        }

        [Fact]
        public void Caution_CodeThatChangedNothing_BesideARealFailure_StaysAmber()
        {
            Assert.True(TurnOf((ToolNames.RunRevitCode, Unchanged()), (ToolNames.SetParameters, WriteEnv(EnvelopeStatus.Partial))).Caution);   // 9 of 12 applied
            Assert.True(TurnOf((ToolNames.RunRevitCode, Unchanged()), (ToolNames.RunRevitCode, WriteEnv(EnvelopeStatus.Failed, EnvelopePhase.Commit))).Caution);
        }

        [Fact]
        public void Caution_ASetParametersNoop_StaysAmber()
        {
            // set_parameters writes nothing also when every target was skipped (read-only, not owned): not a deliberate stop.
            var noop = Unchanged();
            Assert.True(TurnOf((ToolNames.SetParameters, noop)).Caution);
        }

        // ---- Export chips: content-based merge (pr checker, AI-269) -------------------------------------------

    }
}
