using System.Collections.Generic;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    // Pane defects found in the golden-set screenshots (reliability loop, 5 Nov).
    public class PanePolishTests
    {
        // ---- tool-call markup never shows ------------------------------------------------------------

        [Fact]
        public void ToolMarkup_DsmlBlock_IsRemoved_TextBeforeStays()
        {
            var raw = "You're right — let me re-read those numbers.\n\n<｜｜DSML｜｜ calls>\n<｜｜DSML｜｜ invoke name=\"inspect\">\n"
                + "<｜｜DSML｜｜ parameter name=\"target\" string=\"false\">{\"handle\": \"h_a753524e\"}</｜｜DSML｜｜ parameter>\n</｜｜DSML｜｜ invoke>\n</｜｜DSML｜｜ calls>";
            Assert.Equal("You're right — let me re-read those numbers.", ToolMarkup.Strip(raw));
            Assert.True(ToolMarkup.Contains(raw));
        }

        [Fact]
        public void ToolMarkup_TextAfterAClosedBlock_Stays()
        {
            var raw = "Checking.\n<|DSML|calls><|DSML|invoke name=\"x\"></|DSML|invoke></|DSML|calls>\nDone: 62 doors.";
            var s = ToolMarkup.Strip(raw);
            Assert.DoesNotContain("DSML", s);
            Assert.Contains("Done: 62 doors.", s);
            Assert.StartsWith("Checking.", s);
        }

        [Theory]
        [InlineData("Reading now.\n<｜｜DSM")]
        [InlineData("Reading now.\n<｜｜DSML｜｜ calls>\n<｜｜DSML｜｜ inv")]
        [InlineData("Reading now.\n<function_calls>\n<invoke name=\"x\">")]
        [InlineData("Reading now.\n<｜")]
        public void ToolMarkup_UnfinishedBlockWhileStreaming_IsCut(string raw)
        {
            Assert.Equal("Reading now.", ToolMarkup.Strip(raw));
        }

        [Theory]
        [InlineData("62 doors in the host model.")]
        [InlineData("Use a < b and a | b in the rule; the \"DSML\" word alone is fine.")]
        [InlineData("")]
        public void ToolMarkup_PlainText_IsTheSameString(string raw)
        {
            Assert.Same(raw, ToolMarkup.Strip(raw));
            Assert.False(ToolMarkup.Contains(raw));
        }

        // ---- evidence line: counts that say what they count ---------------------------------------------

        private static EvidenceItem Read(int n, params string[] cats)
        {
            var e = new EvidenceItem { Tool = "find_elements", Count = n };
            e.Sources.Add("host");
            foreach (var c in cats) e.Categories.Add(c);
            return e;
        }

        [Fact]
        public void EvidenceLine_ReadsOfDifferentCategories_NameTheirCategory()
        {
            var line = EvidenceSummary.Line(new[] { Read(104, "Doors", "Windows"), Read(62, "Doors"), Read(42, "Windows") }, ReceiptLanguage.En);
            Assert.Equal("3 searches · 104 Doors/Windows, 62 Doors, 42 Windows found · searched host", line);
        }

        [Fact]
        public void EvidenceLine_ManyReads_StopAtFourCounts()
        {
            var items = new List<EvidenceItem>();
            for (var i = 1; i <= 6; i++) items.Add(Read(i * 10, "Doors"));
            var line = EvidenceSummary.Line(items, ReceiptLanguage.En);
            Assert.Equal("6 searches · 10, 20, 30, 40, … found · searched host", line);
            Assert.Equal("6 carian · 10, 20, 30, 40, … dijumpai · dicari host", EvidenceSummary.Line(items, ReceiptLanguage.Ms));
        }

        // ---- check rows with the same cite are not copies of each other ---------------------------------

        private static ResultTable Rules(params string[] idAndCite)
        {
            var t = new ResultTable
            {
                Kind = ResultTable.CheckKind, Title = "Check ubbl 1",
                Columns = { "Rule", "Cite", "Severity", "Pass", "Fail", "Not verified", "Why not verified" },
            };
            for (var i = 0; i < idAndCite.Length; i += 2)
                t.Rows.Add(new List<string> { idAndCite[i], idAndCite[i + 1], "warning", "11", "4", "0", "" });
            return t;
        }

        [Fact]
        public void CheckRuleLine_RepeatedCite_PointsAtTheFirstRowThatHadIt()
        {
            var cite = "UBBL 1984 by-law 106(1): \"the rise of any staircase shall be not more than 180 millimetres\"";
            var t = Rules("UB-01", cite, "UB-02", cite, "UB-03", "UBBL 1984 by-law 107");
            Assert.StartsWith("UB-01 · UBBL 1984 by-law 106(1)", ReceiptTemplates.CheckRuleLine(t, 0, ReceiptLanguage.En));
            Assert.Equal("UB-02 · same cite as UB-01 — 4 fail · 11 pass", ReceiptTemplates.CheckRuleLine(t, 1, ReceiptLanguage.En));
            Assert.Equal("UB-02 · petikan sama seperti UB-01 — 4 gagal · 11 lulus", ReceiptTemplates.CheckRuleLine(t, 1, ReceiptLanguage.Ms));
            Assert.Equal("UB-03 · UBBL 1984 by-law 107 — 4 fail · 11 pass", ReceiptTemplates.CheckRuleLine(t, 2, ReceiptLanguage.En));
            Assert.Contains("rise of any staircase", ReceiptTemplates.CheckRuleFullLine(t, 1, ReceiptLanguage.En));   // the full cite stays behind Details
        }

        // ---- ask form: an empty form is not an answer ----------------------------------------------------

        [Fact]
        public void AskForm_AllFieldsBlank_IsNotAnAnswer()
        {
            Assert.False(AskCardModel.HasAnyAnswer(new Dictionary<string, string> { ["level"] = "" }));
            Assert.False(AskCardModel.HasAnyAnswer(new Dictionary<string, string> { ["level"] = "  ", ["room"] = "" }));
            Assert.True(AskCardModel.HasAnyAnswer(new Dictionary<string, string> { ["level"] = "l1", ["room"] = "" }));
        }
    }
}
