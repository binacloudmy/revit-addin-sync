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

        [Fact]
        public void Plain_RemovesTheRegexOperatorAndGuids_ForTheDrafter()
        {
            Assert.Equal("category = Doors and type matches “(PTh300a) 750 x 2100 sd-d”", EvidenceSummary.Plain("category = Doors AND type ~ '(PTh300a) 750 x 2100 sd-d'"));
            Assert.Equal("element …", EvidenceSummary.Plain("element 3f2504e0-4f89-11d3-9a0c-0305e82c3301"));
            Assert.Equal("", EvidenceSummary.Plain(null));
        }

        [Fact]
        public void Line_OneRead_IsOneLine_WithNoFilterInIt()
        {
            var line = EvidenceSummary.Line(new[] { Read(62, "type ~ 'x'", 62, 0) }, ReceiptLanguage.En);
            Assert.Equal("62 found · searched host · Doors · 62 selected, 0 visible in view", line);
            Assert.DoesNotContain("~", line);
        }

        [Fact]
        public void Line_SeveralReads_AreOneLine_NotOnePerRead()
        {
            var line = EvidenceSummary.Line(new[] { Read(62), Read(3), Read(24) }, ReceiptLanguage.En);
            Assert.Equal("3 searches · 62, 3, 24 found · searched host", line);
            var withSel = EvidenceSummary.Line(new[] { Read(62, "", 62, 0), Read(24) }, ReceiptLanguage.En);
            Assert.Equal("2 searches · 62, 24 found · searched host · 62 selected, 0 visible in view", withSel);   // the counts stay
        }

        [Fact]
        public void Line_AFailedRead_SaysSo()
        {
            var failed = new EvidenceItem { Tool = "inspect", FailedReason = "timeout" };
            Assert.Equal("62 found · searched host · Doors · 1 read failed", EvidenceSummary.Line(new[] { Read(62), failed }, ReceiptLanguage.En));
            Assert.Equal("1 bacaan gagal", EvidenceSummary.Line(new[] { failed }, ReceiptLanguage.Ms));
        }

        [Fact]
        public void NotSearched_SixLinksRepeatedByTwoReads_IsOneLineAboutSixModels()
        {
            var items = new[] { Read(62), Read(24) };
            Assert.Equal(6, EvidenceSummary.NotSearched(items).Count);                 // not 12
            Assert.Equal("6 linked models not searched (unloaded)", EvidenceSummary.NotSearchedLine(items, ReceiptLanguage.En));
            Assert.Equal("6 model terpaut tidak dicari (tidak dimuatkan)", EvidenceSummary.NotSearchedLine(items, ReceiptLanguage.Ms));
            Assert.Equal("", EvidenceSummary.NotSearchedLine(new[] { new EvidenceItem { Count = 1 } }, ReceiptLanguage.En));
        }

        [Fact]
        public void Details_HoldTheNames_EachOnce_FilterInPlainWords()
        {
            var a = Read(62, "category = Doors AND type ~ 'PT'");
            var b = Read(62, "category = Doors AND type ~ 'PT'");
            var details = EvidenceSummary.Details(new[] { a, b }, ReceiptLanguage.En);
            Assert.Single(details, d => d.StartsWith("62 found"));                        // the same read is one line
            Assert.Contains(details, d => d.Contains("type matches “PT”") && !d.Contains("~"));
            Assert.Equal(6, details.Count(d => d.StartsWith("Not searched: ")));
            Assert.Contains("Not searched: jkrME22-3_(BEdeA1_14-001)_A1_w-01_(K)_FP (unloaded)", details);
        }

        [Fact]
        public void ReplyTables_HaveRowsFirstHeaderAndFirstCell()
        {
            var reply = "Total:\n\n| Level | Rooms |\n|---|---|\n| Aras 01 | 23 |\n| Aras 02 | 3 |\n| Total | 26 |\n\nNotes:\n- a | b\n\n| A |\n| :--- |\n| x |\n";
            var tables = EvidenceSummary.ReplyTables(reply);
            Assert.Equal(new[] { 3, 1 }, tables.Select(t => t.Rows));
            Assert.Equal(new[] { "Level", "A" }, tables.Select(t => t.Header));
            Assert.Equal(new[] { "Aras 01", "x" }, tables.Select(t => t.FirstCell));
            Assert.Empty(EvidenceSummary.ReplyTables("62 doors."));
            Assert.Empty(EvidenceSummary.ReplyTables(null));
        }

        private static ResultTable Table(string title, int rows, string column = "Mark") => new ResultTable
        {
            Title = title, Columns = { column },
            Rows = Enumerable.Range(0, rows).Select(i => new List<string> { "r" + i }).ToList(),
        };

        private static EvidenceSummary.ReplyTable Reply(int rows, string header, string first = "") =>
            new EvidenceSummary.ReplyTable { Rows = rows, Header = header, FirstCell = first };

        [Fact]
        public void ExportTables_TheSameTableTwice_IsOneChip()
        {
            var tables = EvidenceSummary.ExportTables(new[] { Table("62 Doors", 62), Table("62 Doors", 62), Table("24 elements", 24) }, null, 3);
            Assert.Equal(new[] { "62 Doors", "24 elements" }, tables.Select(t => t.Title));
        }

        [Fact]
        public void ExportTables_ATableTheReplyAlreadyExports_IsNotOfferedAgain()
        {
            var tables = EvidenceSummary.ExportTables(new[] { Table("26 Rooms", 26, "Room"), Table("62 Doors", 62) }, new[] { Reply(26, "Room") }, 3);
            Assert.Equal(new[] { "62 Doors" }, tables.Select(t => t.Title));
            // the first cell can match too (the reply's header is worded differently)
            var byCell = EvidenceSummary.ExportTables(new[] { Table("26 Rooms", 26, "Room") }, new[] { Reply(26, "Name", "r0") }, 3);
            Assert.Empty(byCell);
        }

        [Fact]
        public void ExportTables_TwoDifferentTablesOfTheSameSize_BothKeepTheirExport_OnlyTheOneShownGoes()
        {
            // a 5-level summary in the reply must not hide the Export of 5 door rows
            var levels = Table("5 levels", 5, "Level");
            var doors = Table("5 doors", 5, "Mark");
            var reply = new[] { Reply(5, "Level", "Aras Tanah") };
            var kept = EvidenceSummary.ExportTables(new[] { levels, doors }, reply, 3);
            Assert.Equal(new[] { "5 doors" }, kept.Select(t => t.Title));                  // the doors keep theirs
            Assert.Equal(2, EvidenceSummary.ExportTables(new[] { levels, doors }, new[] { Reply(5, "Rooms", "Gegelung") }, 3).Count);   // nothing matches: both
            Assert.Equal(2, EvidenceSummary.ExportTables(new[] { levels, doors }, null, 3).Count);
        }

        [Fact]
        public void ExportTables_CheckResultsStay_EmptyTablesGo_AndTheLimitHolds()
        {
            var check = new ResultTable { Title = "Rules", Kind = ResultTable.CheckKind, Columns = { "Rule" }, Rows = { new List<string> { "x" } } };
            var tables = EvidenceSummary.ExportTables(new[] { Table("none", 0), check, Table("a", 5), Table("b", 6), Table("c", 7) }, new[] { Reply(5, "Mark") }, 2);
            Assert.Equal(new[] { "Rules", "b" }, tables.Select(t => t.Title));
        }

        [Fact]
        public void ExportName_IsShort_NoTrailingColon_OrDots()
        {
            Assert.Equal("62 Doors in the host model", EvidenceSummary.ExportName(new ResultTable { Title = "62 Doors in the host model: 60 Aras 01, 2 Aras 02" }));
            Assert.Equal("24 elements from 2 queries", EvidenceSummary.ExportName(new ResultTable { Title = "24 elements from 2 queries ..." }));
            Assert.True(EvidenceSummary.ExportName(new ResultTable { Title = new string('x', 80) }).Length <= 28);
        }

        [Theory]
        [InlineData("I'll count the doors in the host model.", "62 doors", "\n\n")]
        [InlineData("Done.", "\n\nNext", "")]
        [InlineData("Done.\n", "Next", "\n")]
        [InlineData("Done.\n\n", "Next", "")]
        [InlineData("", "Next", "")]
        [InlineData("   ", "Next", "")]
        [InlineData("Done.", "", "")]
        public void ReplySeam_Gap_IsABlankLineOnlyWhenNoneIsThere(string before, string next, string gap)
        {
            Assert.Equal(gap, ReplySeam.Gap(before, next));
        }

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

        [Fact]
        public void ExportTables_TwoTablesWithTheSameTitleColumnsAndRowCount_AreOneChip()
        {
            var a = Table("24 Doors where Width < 900", 24);
            var b = Table("24 Doors where Width < 900", 24);
            b.Rows[12] = new List<string> { "different middle row" };      // same title, columns and size: still one table
            Assert.Single(EvidenceSummary.ExportTables(new[] { a, b }, null, 3));
        }

        [Fact]
        public void ExportTables_SameChipLabelAndSize_IsOneChip_ButADifferentSizeOrNameIsNot()
        {
            var a = Table("24 Doors where Width < 900 (JKR): q1", 24, "Mark");
            var b = Table("24 Doors where Width < 900 (JKR): q2", 24, "Level");   // same label "24 Doors where Width < 900 (JKR)", same size
            var c = Table("62 Doors in the host model", 62);
            var d = Table("24 Windows", 24);
            var kept = EvidenceSummary.ExportTables(new[] { a, b, c, d }, null, 5);
            Assert.Equal(new[] { a.Title, c.Title, d.Title }, kept.Select(t => t.Title));
        }

        [Fact]
        public void ExportTables_ChecksAreOneChipPerRuleSet()
        {
            ResultTable Check(string title) => new ResultTable { Title = title, Kind = ResultTable.CheckKind, Columns = { "Rule" }, Rows = { new List<string> { "x" } } };
            var kept = EvidenceSummary.ExportTables(new[] { Check("Check a 1"), Check("Check a 1"), Check("Check b 1") }, null, 5);
            Assert.Equal(new[] { "Check a 1", "Check b 1" }, kept.Select(t => t.Title));
        }

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
    }
}