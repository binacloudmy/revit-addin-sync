using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-421: a checker remark follows the language (the Borang is English only). Only REMARK
    /// text is covered; The identical "Semak manual" LABEL is out of scope and is
    /// not asserted here (LiveFixTextTests keeps it).
    /// </summary>
    public class RemarkLanguageTests
    {
        private readonly ITestOutputHelper _out;
        public RemarkLanguageTests(ITestOutputHelper o) { _out = o; }

        // What the BIM010 checker writes for a row it has no check for (AuditTools.cs:116).
        private const string KnownBmRemark =
            "Tiada semakan automatik untuk baris ini. Konteks model: 0 workset. Semak manual.";
        // What the grid checker writes (AuditCheckers.BasePoint).
        private const string GridBmRemark =
            "Semakan perlukan Project Base Point berada <=10 mm dari persilangan grid 'A' dan '1'. "
            + "Model ada 3 grid (G1, G2, G3) tetapi tiada yang bernama 'A' atau '1'"
            + " (atau berawalan projek, cth 'PT-A' / 'PT1'). Semak manual.";

        private static IssueVm ManualIssue(string key, string bmRemark) => new IssueVm
        {
            Id = "BIM010-" + key, CheckId = "BIM010-" + key, BorangKey = key, Title = key + " · row",
            Description = bmRemark, Verification = IssueVerification.Human, Status = IssueStatus.Open,
            Element = new ElementRef { Id = "1", Name = "x" }, Spec = new SpecRef { Doc = "" },
        };

        // The Borang is documented "English only, whatever the pane's language" (BorangDoc.cs:58).
        [Fact]
        public void Borang_remark_of_a_known_checker_row_is_in_English()
        {
            var summary = AuditSummary.Build(new[]
            {
                new Bim010Cell { Key = "A3", Verdict = Bim010Verdict.Manual, Note = KnownBmRemark },
            });
            var doc = BorangDoc.Build(summary, new[] { ManualIssue("A3", KnownBmRemark) }, null);
            var remark = doc.SecA[2].Remark;
            _out.WriteLine("Borang A3 remark: " + remark);
            // English-only form: the checker's sentence must not be printed in Malay.
            Assert.DoesNotContain("Tiada semakan automatik", remark);
            Assert.DoesNotContain("Konteks model", remark);
            Assert.Contains("No automatic check for this row", remark);
        }

        [Fact]
        public void Spec_summary_path_does_not_print_the_Malay_remark()
        {
            var s = SpecBorangTests.Summary(new SpecResult
            {
                Source = SpecReport.Audit, IssueId = "BIM010-A3", CheckerId = "x", RowKey = "A3",
                Status = "manual", Note = KnownBmRemark, NoteEn = PaneText.AuditRemarkEn(KnownBmRemark) ?? "",
            });
            var doc = BorangDoc.Build(s, new[] { ManualIssue("A3", KnownBmRemark) }, null);
            var remark = doc.SecA[2].Remark;
            _out.WriteLine("Borang A3 remark (spec summary): " + remark);
            Assert.DoesNotContain("Tiada semakan automatik", remark);
            Assert.DoesNotContain("Konteks model", remark);
        }

        [Fact]
        public void Grid_checker_remark_has_english_text()
        {
            var t = PaneText.AuditRemark(GridBmRemark);
            Assert.Equal(GridBmRemark, t.In(PanelStrings.Ms));
            Assert.Equal("The check needs the Project Base Point to be within 10 mm of the intersection of grids 'A' and '1'. "
                + "The model has 3 grids (G1, G2, G3) but none is named 'A' or '1'"
                + " (or prefixed with the project, e.g. 'PT-A' / 'PT1'). Semak manual.", t.In(PanelStrings.En));
        }

        [Fact]
        public void Grid_checker_other_remarks_have_english_text()
        {
            Assert.Equal("Grid 'A' and grid '1' are parallel in plan, so there is no intersection to measure — semak manual.",
                PaneText.AuditRemarkEn("Grid 'A' dan grid '1' selari pada pelan, jadi tiada persilangan untuk diukur — semak manual."));
            var en = PaneText.AuditRemarkEn("Semakan perlukan Project Base Point berada <=10 mm dari persilangan grid 'A' dan '1'. "
                + "Project Base Point tidak dijumpai. Grid 'A' dan '1' bukan garisan lurus, jadi persilangan tidak boleh dihitung. Semak manual.");
            Assert.Contains("The Project Base Point was not found.", en);
            Assert.Contains("Grid 'A' and '1' is not a straight line", en);
            Assert.DoesNotContain("Grid 'A' dan", en);
        }

        [Fact]
        public void Unknown_remark_stays_as_written_in_both_languages()
        {
            const string other = "Format dijangka: x; dijumpai: y.";
            Assert.Null(PaneText.AuditRemarkEn(other));
            Assert.Equal(other, PaneText.AuditRemark(other).In(PanelStrings.En));
        }

        [Fact]
        public void Section_heading_remark_has_english_text()
        {
            const string heading = "Tajuk seksyen — tiada kriteria untuk diskor. Lihat baris di bawah.";
            Assert.Equal("Section heading — no criteria to score. See the rows below.", PaneText.AuditRemark(heading).In(PanelStrings.En));
            Assert.Equal(heading, PaneText.AuditRemark(heading).In(PanelStrings.Ms));
            Assert.Equal("Section heading — no criteria to score. Model context: 2 worksets. See the rows below.",
                PaneText.AuditRemarkEn("Tajuk seksyen — tiada kriteria untuk diskor. Konteks model: 2 workset. Lihat baris di bawah."));
        }

        [Fact]
        public void Borang_remark_of_a_grid_row_is_in_English()
        {
            var summary = AuditSummary.Build(new[] { new Bim010Cell { Key = "A3", Verdict = Bim010Verdict.Manual, Note = GridBmRemark } });
            var doc = BorangDoc.Build(summary, new[] { ManualIssue("A3", GridBmRemark) }, null);
            var remark = doc.SecA[2].Remark;
            Assert.Contains("The model has 3 grids", remark);
            Assert.DoesNotContain("Semakan perlukan", remark);
        }

        // AuditCheckers needs a live Revit Document and cannot run here. So this pins the
        // checker SOURCE: every BM fragment the translator expects must still be written
        // by the checker. If someone rewords a remark there, this fails and the English
        // (PaneText.AuditRemarkEn) must be updated with it.
        private static string SourceOf(string relative)
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, relative))) dir = dir.Parent;
            Assert.True(dir != null, "source file not found: " + relative);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, relative));
        }

        [Fact]
        public void Checker_source_still_writes_the_remarks_the_translator_knows()
        {
            var checkers = SourceOf(@"BinaVibe\Mcp\Tools\Audit\AuditCheckers.cs");
            foreach (var frag in new[]
            {
                "Project Base Point berada <=10 mm dari persilangan grid 'A' dan '1'",
                "$\"Semakan perlukan {rule}. \"", "Project Base Point tidak dijumpai. ", " tetapi tiada yang bernama ",
                "(atau berawalan projek, cth 'PT-A' / 'PT1'). ", "bukan garisan lurus, jadi persilangan ",
                "tidak boleh dihitung. ", "+ \"Semak manual.\"", "selari pada pelan, jadi tiada ",
                "persilangan untuk diukur — semak manual.", " atau ", " dan ",
            })
                Assert.Contains(frag, checkers);

            var tools = SourceOf(@"BinaVibe\Mcp\Tools\Audit\AuditTools.cs");
            foreach (var frag in new[]
            {
                "Tajuk seksyen — tiada kriteria untuk diskor. ", " Lihat baris di bawah.",
                "Tiada semakan automatik untuk baris ini. ", " Semak manual.", "Tiada semakan automatik untuk baris ini — semak manual.",
            })
                Assert.Contains(frag, tools);
        }
    }
}
