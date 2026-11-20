using System;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-469: the legacy BIM010 checker (AuditCheckers.cs) writes every remark in BM. Once a
    /// spec verdict replaces the legacy issue for a row (PR #303), the remarks that still reach
    /// the Issues list are those of the checkers SpecReport.MapAudit maps to no spec check
    /// (views_template, area_plans, schedules_required), "nothing to check" for the graphical
    /// views, and "the checker failed". Each has an English text; every other template stays
    /// BM (listed in Tests/data/spec_drift_audit.md). The BM strings below are the checker's,
    /// word for word — a guard test fails if AuditCheckers.cs changes them.
    /// </summary>
    public class LegacyRemarkEnglishTests
    {
        private const string Ms = PanelStrings.Ms;
        private const string En = PanelStrings.En;

        private static string Checkers() => Read("AuditCheckers.cs");

        private static string Read(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.True(dir != null, "repo root not found from " + AppContext.BaseDirectory);
            return File.ReadAllText(Path.Combine(dir.FullName, "BinaVibe", "Mcp", "Tools", "Audit", file)).Replace("\r\n", "\n");
        }

        [Fact]
        public void The_checker_still_writes_the_templates_the_translations_match()
        {
            var src = Checkers();
            foreach (var piece in new[]
            {
                "Peraturan: {rule}. Dijumpai 0 Area Plan dalam model — jana analisis ruang/zon ",
                "const string rule = \"sekurang-kurangnya satu Area Plan untuk analisis ruang/zon\";",
                "Tiada nama jadual yang sepadan untuk: ",
                "jadual dalam model disemak). ",
                "\"Jana jadual tersebut.\"",
                "const string rule = \"setiap view grafik ada View Template\";",
                "view tiada template (cth: ",
                "Sapukan template ",
                "daripada templat seni bina.",
                "Wujudkan template jenis itu dahulu jika dikehendaki, kemudian semak semula.",
            })
                Assert.True(src.Contains(piece), "AuditCheckers.cs no longer contains: " + piece);
            Assert.Contains("\"Semakan automatik gagal (\" + checker.Id + \") — semak manual.\"", Read("AuditTools.cs"));
            Assert.Contains("Tiada {population} dalam model, jadi peraturan", src);
        }

        [Fact]
        public void Area_plans_remark()
        {
            var bm = "Peraturan: sekurang-kurangnya satu Area Plan untuk analisis ruang/zon. Dijumpai 0 Area Plan dalam model — jana analisis ruang/zon daripada Area Plan.";
            var t = PaneText.AuditRemark(bm);
            Assert.Equal(bm, t.In(Ms));
            Assert.Equal("Rule: at least one Area Plan for space / zone analysis. Found 0 Area Plans in the model — generate the space / zone analysis from an Area Plan.", t.In(En));
        }

        [Fact]
        public void Schedules_remark_keeps_the_checkers_own_token_list()
        {
            var rule = "nama jadual mengandungi (termasuk awalan JKR): "
                + string.Join(" / ", AuditMatching.RequiredSchedules.Select(r => r.label + " (" + string.Join("|", r.tokens) + ")"))
                + " / jadual komponen (door|window|wall|floor|ceiling|roof|stair|railing + schedule)";
            var bm = $"Peraturan: {rule}. Tiada nama jadual yang sepadan untuk: Schedule of Accommodation, Material Takeoff Schedule (7 jadual dalam model disemak). Jana jadual tersebut.";
            var en = PaneText.AuditRemark(bm).In(En);
            Assert.StartsWith("Rule: a schedule name contains (including the JKR prefix): Schedule of Accommodation (accommodation|", en);
            Assert.Contains(" / a component schedule (door|window|wall|floor|ceiling|roof|stair|railing + schedule). ", en);
            Assert.EndsWith("No schedule name matches: Schedule of Accommodation, Material Takeoff Schedule (7 schedules in the model checked). Generate those schedules.", en);
            Assert.Equal(bm, PaneText.AuditRemark(bm).In(Ms));
        }

        [Fact]
        public void Views_template_remark_with_and_without_the_unactionable_clause_and_the_full_list_note()
        {
            var plain = "Peraturan: setiap view grafik ada View Template. 4/25 view tiada template (cth: A, B, C). Sapukan template daripada templat seni bina.";
            Assert.Equal("Rule: every graphical view has a View Template. 4/25 views have no template (e.g. A, B, C). Apply the template from the architectural template.",
                PaneText.AuditRemark(plain).In(En));

            var full = plain
                + " 2 boleh tindakan, 2 tiada template jenis tersebut wujud dalam model (Schedule ×2) — tidak boleh disapukan sehingga template jenis itu diwujudkan."
                + " Senarai penuh 20 item ada dalam draft_export (element_ids).";
            Assert.Equal("Rule: every graphical view has a View Template. 4/25 views have no template (e.g. A, B, C). Apply the template from the architectural template."
                + " 2 can be fixed, 2 have no template of their view type in the model (Schedule ×2) — they cannot be applied until a template of that type exists."
                + " The full list of 20 items is in draft_export (element_ids).", PaneText.AuditRemark(full).In(En));
        }

        [Fact]
        public void Views_template_not_verifiable_remark()
        {
            var bm = "Peraturan: setiap view grafik ada View Template. 20/25 view ada template; 5 view tiada template tetapi tiada template jenis tersebut wujud dalam model (Legend ×5; cth: L1, L2) — tidak boleh tindakan. Wujudkan template jenis itu dahulu jika dikehendaki, kemudian semak semula.";
            Assert.Equal("Rule: every graphical view has a View Template. 20/25 views have a template; 5 have none, and no template of their view type exists in the model (Legend ×5; e.g. L1, L2) — nothing can be done yet. Create a template of that type first if wanted, then check again.",
                PaneText.AuditRemark(bm).In(En));
        }

        [Fact]
        public void Nothing_to_check_for_graphical_views_and_a_crashed_checker()
        {
            Assert.Equal("There are no graphical views in the model, so the rule \"every graphical view has a View Template\" has nothing to check (0 checked) — semak manual.",
                PaneText.AuditRemark("Tiada view grafik dalam model, jadi peraturan \"setiap view grafik ada View Template\" tiada data untuk disemak (0 diperiksa) — semak manual.").In(En));
            Assert.Equal("The automatic check failed (area_plans) — semak manual.",
                PaneText.AuditRemark("Semakan automatik gagal (area_plans) — semak manual.").In(En));
        }

        [Fact]
        public void A_remark_that_is_not_one_of_these_stays_as_written()
        {
            var other = "Peraturan: sekurang-kurangnya satu view Legend. Dijumpai 0 view Legend dalam model.";
            Assert.Equal(other, PaneText.AuditRemark(other).In(En));
            var edited = "Peraturan: sekurang-kurangnya satu Area Plan untuk analisis ruang/zon. Dijumpai 0 Area Plan dalam model.";
            Assert.Equal(edited, PaneText.AuditRemark(edited).In(En));
        }
    }
}
