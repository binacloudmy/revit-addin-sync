using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Step 5: the built-in BIM010 form (rows, keys, headings), fill_audit records
    /// and backend checks as cells, and the section-and-cell summary (section %,
    /// Borang row marks, unmapped checks).
    /// </summary>
    public class Bim010SummaryTests
    {
        private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);

        private static List<string[]> Tsv() =>
            File.ReadAllLines(Data("bim010_expected_rows.tsv"))
                .Where(l => l.Trim().Length > 0 && !l.StartsWith("#"))
                .Select(l => l.Split('\t')).ToList();

        // ── The form ──

        [Fact]
        public void The_built_in_form_has_the_50_verified_rows_in_order()
        {
            var tsv = Tsv();
            Assert.Equal(50, Bim010Form.Rows.Count);
            Assert.Equal(tsv.Count, Bim010Form.Rows.Count);
            for (int i = 0; i < tsv.Count; i++)
            {
                Assert.Equal(tsv[i][0], Bim010Form.Rows[i].Section);
                Assert.Equal(tsv[i][1], Bim010Form.Rows[i].RowRef);
                Assert.Equal(tsv[i][2], Bim010Form.Rows[i].Description);
            }
        }

        [Fact]
        public void The_built_in_form_matches_the_parsed_pdf()
        {
            var parsed = AuditFormParser.Parse(Data("bim010_model_audit_form.pdf"));
            Assert.Equal(parsed.Select(r => r.Section + "|" + r.RowRef),
                         Bim010Form.Rows.Select(r => r.Section + "|" + r.RowRef));
        }

        [Fact]
        public void Built_in_rows_reach_the_same_checkers_as_the_parsed_pdf()
        {
            var parsed = AuditFormParser.Parse(Data("bim010_model_audit_form.pdf"));
            var builtIn = Bim010Form.ToFormRows();
            for (int i = 0; i < parsed.Count; i++)
                Assert.Equal(AuditMatching.Match(parsed[i]), AuditMatching.Match(builtIn[i]));
        }

        [Fact]
        public void Six_x_point_0_rows_are_headings_and_44_rows_are_scored()
        {
            Assert.Equal(new[] { "C1.0", "C2.0", "C3.0", "C4.0", "C5.0", "E1.0" },
                         Bim010Form.Rows.Where(r => r.IsHeading).Select(r => r.Key));
            Assert.Equal(44, Bim010Form.ScoredRows.Count());
        }

        [Theory]
        [InlineData("A", "1", "A1")]
        [InlineData("B", "1", "B1")]
        [InlineData("C", "1.1", "C1.1")]
        [InlineData("C", "4.2", "C4.2")]
        [InlineData("D", "10", "D10")]
        [InlineData("E", "1.2", "E1.2")]
        public void Form_numbers_map_to_prototype_keys(string section, string rowRef, string key)
        {
            Assert.Equal(key, Bim010Form.KeyOf(section, rowRef));
            Assert.NotNull(Bim010Form.Find(key));
            Assert.Equal(section, Bim010Form.SectionOf(key));
        }

        [Fact]
        public void Keys_are_unique_and_unknown_keys_are_not_found()
        {
            Assert.Equal(Bim010Form.Rows.Count, Bim010Form.Rows.Select(r => r.Key).Distinct().Count());
            Assert.Null(Bim010Form.Find("C9.9"));
            Assert.Equal("", Bim010Form.SectionOf("Z1"));
        }

        [Fact]
        public void Section_d_has_23_family_categories()
        {
            Assert.Equal(23, Bim010Form.DCategories.Count);
            Assert.Equal("D10", Bim010Form.DKeyFor("Doors"));
            Assert.Equal("D21", Bim010Form.DKeyFor("structural column"));
            Assert.Null(Bim010Form.DKeyFor("Lighting Fixtures"));
        }

        // ── fill_audit records → cells ──

        private static AuditRecord Rec(string section, string rowRef, string compliance, string reason = null, string remark = "r")
        {
            var rec = new AuditRecord
            {
                Row = new AuditFormRow { Section = section, RowRef = rowRef, Description = "d" },
                Compliance = compliance,
                Remark = remark,
            };
            if (reason != null) rec.Evidence = new Dictionary<string, object> { ["not_verifiable_reason"] = reason };
            return rec;
        }

        [Theory]
        [InlineData("yes", Bim010Verdict.Pass)]
        [InlineData("no", Bim010Verdict.Fail)]
        [InlineData("not_verifiable", Bim010Verdict.Manual)]
        [InlineData("", Bim010Verdict.Manual)]
        public void Fill_audit_compliance_maps_to_a_verdict(string compliance, Bim010Verdict verdict)
        {
            Assert.Equal(verdict, AuditSummary.VerdictOf(compliance));
        }

        [Fact]
        public void Headings_and_not_applicable_rows_give_no_cell()
        {
            var cells = AuditSummary.CellsFromRecords(new[]
            {
                Rec("C", "1.0", "not_verifiable"),
                Rec("D", "7", "not_verifiable", "category_not_present"),
                Rec("D", "8", "not_verifiable", "zero_placed_instances"),
                Rec("D", "9", "not_verifiable", "no_types_to_check"),
                Rec("C", "1.4", "not_verifiable", "no_template_of_view_type_in_model"),
                Rec("A", "4", "no"),
            });
            Assert.Equal(new[] { "C1.4", "A4" }, cells.Select(c => c.Key));
            Assert.Equal(Bim010Verdict.Manual, cells[0].Verdict);
            Assert.Equal(Bim010Verdict.Fail, cells[1].Verdict);
        }

        [Fact]
        public void Section_d_records_are_the_naming_column()
        {
            var cell = Assert.Single(AuditSummary.CellsFromRecords(new[] { Rec("D", "10", "no") }));
            Assert.Equal("D10", cell.Key);
            Assert.Equal(Bim010Column.Naming, cell.Column);
            Assert.Equal("audit", cell.Source);
        }

        // ── Summary ──

        private static Bim010Cell C(string key, Bim010Verdict v, string note = "", string col = "") =>
            new Bim010Cell { Key = key, Verdict = v, Note = note, Column = col };

        [Fact]
        public void Section_percent_is_pass_over_ai_cells_and_manual_is_counted_apart()
        {
            var s = AuditSummary.Build(new[]
            {
                C("A2", Bim010Verdict.Pass), C("A4", Bim010Verdict.Pass), C("A5", Bim010Verdict.Pass),
                C("A6", Bim010Verdict.Fail), C("A1", Bim010Verdict.Manual), C("A3", Bim010Verdict.Manual),
            });
            var a = s.Section("A");
            Assert.Equal(3, a.Pass);
            Assert.Equal(1, a.Fail);
            Assert.Equal(2, a.Pending);
            Assert.Equal(75, a.Pct);
            Assert.Null(s.Section("B").Pct);             // no AI cells → no %
            Assert.Equal(75, s.Pct);
            Assert.Equal(2, s.Pending);
        }

        [Fact]
        public void Adding_manual_cells_does_not_move_any_percent()
        {
            var ai = new List<Bim010Cell> { C("B1", Bim010Verdict.Pass), C("B1", Bim010Verdict.Fail) };
            var before = AuditSummary.Build(ai);
            ai.AddRange(Enumerable.Range(0, 9).Select(_ => C("B1", Bim010Verdict.Manual)));
            var after = AuditSummary.Build(ai);
            Assert.Equal(before.Pct, after.Pct);
            Assert.Equal(before.Section("B").Pct, after.Section("B").Pct);
            Assert.Equal(9, after.Pending);
        }

        [Fact]
        public void A_row_mark_is_x_if_any_fail_else_manual_else_tick()
        {
            var s = AuditSummary.Build(new[]
            {
                C("A4", Bim010Verdict.Pass), C("A4", Bim010Verdict.Manual), C("A4", Bim010Verdict.Fail, "gap 4-10"),
                C("A5", Bim010Verdict.Pass), C("A5", Bim010Verdict.Manual, "levels unreadable"),
                C("A6", Bim010Verdict.Pass),
            });
            Assert.Equal("n", s.Rows["A4"].Mark);
            Assert.Equal("y", s.Rows["A5"].Mark);
            Assert.Equal("y", s.Rows["A6"].Mark);
            Assert.Equal("", s.Rows["A7"].Mark);          // no cells: not assessed
            Assert.False(s.Rows.ContainsKey("C1.0"));     // headings carry no mark
        }

        [Fact]
        public void Notes_list_failures_before_manual_reasons()
        {
            var s = AuditSummary.Build(new[]
            {
                C("C5.1", Bim010Verdict.Manual, "No CDE access."),
                C("C5.1", Bim010Verdict.Fail, "EL link absent."),
                C("C5.1", Bim010Verdict.Fail, "EL link absent."),
            });
            Assert.Equal(new[] { "EL link absent.", "No CDE access." }, s.Rows["C5.1"].Notes);
        }

        [Fact]
        public void Section_d_rows_carry_a_mark_per_column()
        {
            var s = AuditSummary.Build(new[]
            {
                C("D10", Bim010Verdict.Fail, "", Bim010Column.Naming),
                C("D10", Bim010Verdict.Pass, "", Bim010Column.Information),
                C("D10", Bim010Verdict.Manual, "", Bim010Column.Geometry),
            });
            var d = s.Rows["D10"];
            Assert.Equal("n", d.Mark);
            Assert.Equal("n", d.Columns[Bim010Column.Naming]);
            Assert.Equal("y", d.Columns[Bim010Column.Information]);
            Assert.Equal("y", d.Columns[Bim010Column.Geometry]);
            Assert.Equal("", d.Columns[Bim010Column.Quality]);
        }

        [Fact]
        public void Unmapped_checks_are_kept_distinct_and_sorted_and_unknown_keys_ignored()
        {
            var s = AuditSummary.Build(new[] { C("Z9", Bim010Verdict.Fail) }, new[] { "b · x", "a · y", "b · x" });
            Assert.Equal(new[] { "a · y", "b · x" }, s.Unmapped);
            Assert.Equal(0, s.Fail);
        }

        // ── Backend check → row ──

        [Theory]
        [InlineData("Project Naming", "", "File naming convention", "A2", "")]
        [InlineData("Project Base Point", "", "Project Base Point surveyed", "A4", "")]
        [InlineData("Levels", "", "Level naming convention", "A5", "")]
        [InlineData("Grids", "", "Grid naming convention", "A5", "")]
        [InlineData("Project Information", "", "Project number populated", "B1", "")]
        [InlineData("Project Information", "", "Link files are latest version", "C5.1", "")]
        [InlineData("", "", "Model federation", "C5.1", "")]
        [InlineData("", "", "JKR BIM template used", "C1.1", "")]
        [InlineData("", "", "JKR shared parameters loaded", "B1", "")]
        [InlineData("Component Naming", "Doors", "Type name does not follow JKR naming", "D10", "n")]
        [InlineData("Doors", "Doors", "JKR code prefix matches category", "D10", "n")]
        [InlineData("Walls", "Walls", "Empty Type parameter: Kod_Jenis", "D2", "i")]
        [InlineData("Component Parameter", "Structural Columns", "Sistem_jkr_stt value valid", "D21", "i")]
        [InlineData("Casework", "Casework", "Element has a name", "D12", "n")]
        [InlineData("Topography", "Topography", "Material code valid", "D22", "i")]
        public void Backend_checks_map_to_a_borang_row(string category, string revitCategory, string rule, string key, string column)
        {
            var m = Bim010Mapping.Map(category, revitCategory, rule, "element");
            Assert.NotNull(m);
            Assert.Equal(key, m.Value.Key);
            Assert.Equal(column, m.Value.Column);
        }

        [Theory]
        [InlineData("Lighting Fixtures", "Lighting Fixtures", "Element has a name")]
        [InlineData("Component Naming", "", "Type name does not follow JKR naming")]
        [InlineData("", "", "Something unknown")]
        public void Checks_without_a_borang_row_map_to_nothing(string category, string revitCategory, string rule)
        {
            Assert.Null(Bim010Mapping.Map(category, revitCategory, rule, "element"));
        }
    }
}
