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
        public void Department_checker_remark_and_rule_have_english_text()
        {
            const string rule = "setiap bilik (Room) ada nilai parameter 'Department'";
            Assert.Equal("every room (Room) has a value in the 'Department' parameter", PaneText.AuditRuleEn(rule));
            Assert.Equal("Rule: every room (Room) has a value in the 'Department' parameter. 3/10 rooms have no value (e.g. G03, G04, 103)."
                         + " Fill in the Department parameter for those rooms.",
                PaneText.AuditRemarkEn("Peraturan: " + rule + ". 3/10 bilik tiada nilai (cth: G03, G04, 103). Isi parameter Department untuk bilik tersebut."));
            Assert.Equal("Rule: every room (Room) has a value in the 'Department' parameter. All 10 rooms have a Department value — complies.",
                PaneText.AuditRemarkEn("Peraturan: " + rule + ". Semua 10 bilik ada nilai Department — patuh."));
            Assert.Equal("some rule we do not know", PaneText.AuditRuleEn("some rule we do not know"));
        }

        [Theory]
        [InlineData("0", "Columns")]
        [InlineData("0", "structural column")]
        [InlineData("3", "Structural Column")]
        public void Empty_expected_category_remark_has_english_text(string n, string label)
        {
            var bm = $"{n} elemen {label} dijumpai dalam model walaupun kategori ini "
                + "biasanya wajib bagi model seni bina — sahkan sama ada memang "
                + "tiada dalam skop projek. (Kualiti/maklumat/geometri: semak manual.)";
            var en = PaneText.AuditRemarkEn(bm);
            Assert.Equal($"{n} {label} elements found in the model although this category is normally required in an architectural model"
                + " — confirm whether it is really outside the project scope. (Quality/information/geometry: semak manual.)", en);
            Assert.Equal(bm, PaneText.AuditRemark(bm).In(PanelStrings.Ms));
        }

        [Fact]
        public void Empty_optional_category_remark_has_english_text()
        {
            var bm = "0 elemen Columns dijumpai dalam model dan kategori ini tidak wajib — "
                + "baris ini tidak berkenaan, atau semak manual jika sepatutnya ada.";
            Assert.Equal("0 Columns elements found in the model and this category is not required — this row does not apply, or semak manual if there should be some.",
                PaneText.AuditRemarkEn(bm));
        }

        [Fact]
        public void Borang_D_row_remark_of_an_empty_category_is_in_English()
        {
            const string bm = "0 elemen Columns dijumpai dalam model walaupun kategori ini biasanya wajib bagi model seni bina — sahkan sama ada memang tiada dalam skop projek. (Kualiti/maklumat/geometri: semak manual.)";
            var summary = AuditSummary.Build(new[] { new Bim010Cell { Key = "D18", Verdict = Bim010Verdict.Manual, Note = bm } });
            var (text, _) = BorangDoc.RemarkFor("D18", "m", summary, new[] { ManualIssue("D18", bm) });
            Assert.DoesNotContain("dijumpai", text);
            Assert.DoesNotContain("sahkan", text);
            Assert.Contains("elements found in the model", text);
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
                "$\"0 elemen {label} dijumpai dalam model walaupun kategori ini \"", "biasanya wajib bagi model seni bina — sahkan sama ada memang ",
                "tiada dalam skop projek. (Kualiti/maklumat/geometri: semak manual.)", "dijumpai dalam model dan kategori ini tidak wajib — ",
                "baris ini tidak berkenaan, atau semak manual jika sepatutnya ada.",
                "Peraturan: {rule}. ", " bilik tiada nilai (cth: ", "Isi parameter ", "Department untuk bilik tersebut.",
                "Semua {rooms.Count} bilik ada nilai Department — patuh.", "setiap bilik (Room) ada nilai parameter 'Department'",
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

        // AI-421 round 2: the fragments the legacy-remark templates (PaneText.LegacyBody / LegacyBare /
        // LegacyTails / LegacyRuleTemplates) rely on. Source spelling, one literal piece each.
        [Fact]
        public void Checker_source_still_writes_the_legacy_remark_fragments()
        {
            var checkers = SourceOf(@"BinaVibe\Mcp\Tools\Audit\AuditCheckers.cs");
            foreach (var frag in new[]
            {
                // common
                "Tiada {population} dalam model, jadi peraturan ", "data untuk disemak (0 diperiksa) — semak manual.",
                "Senarai penuh {total} item ada dalam draft_export (element_ids).", "(Kualiti/maklumat/geometri: semak manual.)",
                "\"grid, aras atau pelan lantai\"", "\"elemen Walls/Floors/Ceilings/Roofs\"", "\"view grafik\"", "\"Revit link\"", "\"elemen Project Information\"",
                // file naming, base point
                "Format dijangka: {rule}; dijumpai: ", "({segments} segmen) — patuh.", "Cadangan nama: ",
                "Tiada cadangan automatik yang yakin — namakan semula fail secara manual ", "mengikut garis panduan penamaan.",
                "Had dijangka: <=10 mm dari persilangan ", "; dijumpai sisihan ", " mm — patuh.", "Selaraskan kedudukan model ke grid ",
                "pairName.Replace(\"/\", \" dan \")", "$\"grid {pairName} (Grid A/1)\"",
                // grids and levels
                "Dijumpai {grids} grid ({split}), ", "semua bernama unik; semua {plans.Count} pelan lantai terikat pada ", "aras — patuh.",
                "Dijumpai {grids} grid, {levels} aras. ", "\"Tiada grid dalam model\"", "\"Tiada aras dalam model\"",
                "{gridUnnamed.Count}/{grids} grid tanpa nama", "nama grid berganda (cth: ", "{levelUnnamed.Count}/{levels} aras tanpa nama",
                "nama aras berganda (cth: ", "pelan lantai tanpa aras (cth: ", "{gridLetters} huruf, {gridNumbers} nombor",
                ", {gridSubs} sub-grid", "; awalan projek '{gridPrefix}' pada {gridPrefixed} grid",
                // materials
                "elemen pertama semuanya ada material (", "tetapi model melebihi had imbasan jadi elemen selebihnya tidak disemak — ",
                "Imbasan dihadkan kepada {ElementScanCap} elemen; elemen selebihnya tidak disemak.",
                "Semua {scanned} elemen ada material ({breakdown}) — patuh.", "elemen tiada material ",
                "(tanpa material/diimbas: {breakdown}; cth: ", "Tetapkan material pada jenis elemen berkenaan.",
                // project information
                "Dokumen tiada elemen Project Information untuk dibaca — semak manual ", "di Manage > Project Information.",
                "Keenam-enam medan terisi (Project Name", " medan belum dikemaskini: ", "Isi di Manage > Project Information.",
                "name + \" (kosong)\"", " — nilai lalai)",
                // view templates, view sets
                "view ada template — patuh.", "view tiada template (cth: ", "Sapukan template ", "daripada templat seni bina.",
                "view ada template; ", "view tiada template tetapi tiada template jenis tersebut wujud dalam ",
                "model ({split.UnactionableTypesText}; cth: ", "tindakan. Wujudkan template jenis itu dahulu jika dikehendaki, kemudian semak semula.",
                "Baris tidak menyatakan set view (BOMBA/PBT) yang dimaksudkan — semak manual.",
                "setiap view dengan '{token}' dalam namanya ada View Template", "Tiada nama view mengandungi '{token}' daripada ",
                "view yang disemak — wujudkan view {token} dan sapukan ", "\"view template.\"", "view {token} ada template — patuh.",
                "view {token} tiada template ", "template {token}.", " view {token} ada template; ", "view {token} tiada template tetapi tiada template jenis tersebut wujud ",
                "sekurang-kurangnya satu view dengan '{token}' dalam namanya", "Dijumpai {hits.Count} view (cth: ",
                "view yang disemak. Wujudkan view {token} untuk tujuan tersebut.",
                // area plan, legend, schedules
                "Dijumpai {areaPlans.Count} Area Plan (", "Dijumpai 0 Area Plan dalam model — jana analisis ruang/zon ", "daripada Area Plan.",
                "Dijumpai {legends.Count} Legend (", "Dijumpai 0 view Legend dalam model — sediakan Legend untuk ", "komponen dan nota am.",
                "nama jadual mengandungi (termasuk awalan JKR): ", " / jadual komponen (door|window|wall|floor|ceiling|roof|stair|railing + schedule)",
                "Ketiga-tiga jadual wajib dijumpai daripada {names.Count} ", "jadual dalam model (", "Tiada nama jadual yang sepadan untuk: ",
                " jadual dalam model disemak). ", "Jana jadual tersebut.",
                // sheets, title blocks, links
                "Dijumpai 0 Sheet dalam model — sediakan helaian ", "lukisan seni bina.", "Semua {sheets.Count} sheet mengandungi view — patuh.",
                " sheet kosong (cth: ", "Susun view pada sheet berkenaan.", "Dijumpai 0 title block pada mana-mana sheet — ", "gunakan JKR Title Block.",
                "Semua {tbs.Count} title block sepadan — patuh.", " title block tidak sepadan ", "Tukar kepada JKR Title ", "\"Block.\"",
                "Tiada Revit link dalam model, jadi tiada status pautan untuk disemak ", "— sahkan secara manual sama ada pautan memang tidak diperlukan.",
                "Semua {links.Count} link berstatus Loaded — patuh.", " link bukan Loaded: ", "Reload atau betulkan laluan link.",
                // type naming (D rows)
                "{instances.Count} elemen {label} tetapi 0 jenis (ElementType) untuk ", "tidak terpakai di sini — semak ",
                "{instances.Count} elemen {label} tetapi tiada satu pun ", "menggunakan {types.Count} jenis kategori ini (0 contoh diletakkan bagi ",
                "setiap jenis) — tiada nama jenis yang digunakan untuk dinilai; semak ",
                " jenis tanpa contoh diletakkan tidak dikira (lihat unused_types).", " elemen tanpa jenis tidak dapat dinilai.",
                "{instances.Count} elemen, {usedTypes.Count} jenis digunakan — ", "semua nama jenis yang digunakan patuh.",
                " jenis yang digunakan ", "tidak patuh, meliputi {offendingInstances.Count}/{instances.Count} elemen (cth: ", "\"Cadangan: \"",
                " nama tiada cadangan automatik yang yakin — namakan semula ", "secara manual mengikut format JKR. ",
                // rooms (Number and Name)
                "Dijumpai 0 bilik yang diletakkan (placed Room) ", "— {allRooms} Room wujud tetapi tidak diletakkan (Area 0). ", "dalam model. ",
                "Letak Room dahulu sebelum Number/Name boleh disemak. ", "0 bilik diletakkan dalam model dan kategori ini tidak wajib — baris ini ",
                "tidak berkenaan, atau semak manual jika sepatutnya ada.", " bilik diletakkan tetapi parameter ", "Number/Name tidak dapat dibaca — semak manual.",
                "Semua {checkedCount} bilik yang diletakkan ada Number dan Name — patuh.", " bilik tidak dapat dibaca, tidak dikira.)",
                " bilik tidak lengkap ", " tiada Number, ", " tiada Name; cth: ", "Isi Number dan Name untuk bilik tersebut.",
                "setiap bilik (Room) yang diletakkan ada Number dan Name",
            })
                Assert.Contains(frag, checkers);

            Assert.Contains("Semakan automatik gagal (", SourceOf(@"BinaVibe\Mcp\Tools\Audit\AuditTools.cs"));
            Assert.Contains(") — semak manual.", SourceOf(@"BinaVibe\Mcp\Tools\Audit\AuditTools.cs"));

            var availability = SourceOf(@"BinaVibe\Mcp\Tools\Audit\AuditTemplateAvailability.cs");
            foreach (var frag in new[]
            {
                "boleh tindakan, ", " tiada template ", "jenis tersebut wujud dalam model (", ") — tidak boleh ", "disapukan sehingga template jenis itu diwujudkan.",
            })
                Assert.Contains(frag, availability);
        }
    }
}
