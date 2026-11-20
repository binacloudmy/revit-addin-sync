using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The issue-list export (Excel / CSV): the count the Borang preview's
    /// footer shows is the count the export writes with the default options.</summary>
    [Xunit.Collection("PaneLanguage")]
    public class IssueExportRowsTests
    {
        private static IssueVm I(string id, IssueStatus st) => new IssueVm { Id = id, Title = "t" + id, Status = st };

        private static IssueVm[] Sample() => new[]
        {
            I("1", IssueStatus.Open), I("2", IssueStatus.Open), I("3", IssueStatus.Fixed), I("4", IssueStatus.Ignored),
        };

        [Fact]
        public void Default_count_is_every_issue_the_dialog_would_write()
        {
            Assert.Equal(4, IssueExportRows.DefaultCount(Sample()));
            Assert.Equal(0, IssueExportRows.DefaultCount(null));
        }

        [Fact]
        public void Options_pick_open_and_or_resolved()
        {
            var s = Sample();
            Assert.Equal(new[] { "1", "2" }, IssueExportRows.Select(s, true, false).Select(i => i.Id).ToArray());
            Assert.Equal(new[] { "3", "4" }, IssueExportRows.Select(s, false, true).Select(i => i.Id).ToArray());
            Assert.Empty(IssueExportRows.Select(s, false, false));
        }

        [Fact]
        public void Marked_manual_issue_is_written_under_Open_and_counts_match_rows()
        {
            var s = Sample().Concat(new[] { I("5", IssueStatus.ManualFixNeeded) }).ToArray();
            Assert.Equal(new[] { "1", "2", "5" }, IssueExportRows.Select(s, true, false).Select(i => i.Id).ToArray());
            Assert.Equal(new[] { "3", "4" }, IssueExportRows.Select(s, false, true).Select(i => i.Id).ToArray());
            Assert.Equal(s.Length, IssueExportRows.Select(s, true, true).Length);
            Assert.Empty(IssueExportRows.Select(s, false, false));
        }

        /// <summary>Runs the body in the language ("ms" / "en"), then puts the static state back.</summary>
        private static void In(string lang, System.Action body)
        {
            var before = PaneLanguage.Current;
            try { PaneLanguage.Set(lang); body(); }
            finally { PaneLanguage.Set(before); }
        }

        [Fact]
        public void Header_has_nine_columns_in_BM_and_in_EN()
        {
            In("ms", () =>
            {
                Assert.Equal(9, IssueExportRows.Header.Length);
                Assert.Equal("ID Elemen", IssueExportRows.Header[7]);
                Assert.Equal("Kod Peraturan", IssueExportRows.Header[8]);
                Assert.Equal("Tajuk", IssueExportRows.Header[1]);
            });
            In("en", () =>
            {
                Assert.Equal(9, IssueExportRows.Header.Length);
                Assert.Equal("Element ID", IssueExportRows.Header[7]);
                Assert.Equal("Rule code", IssueExportRows.Header[8]);
                Assert.Equal("Title", IssueExportRows.Header[1]);
            });
        }

        [Fact]
        public void Element_id_and_rule_code_cells_are_written()
        {
            var i = new IssueVm { Id = "abc", CheckId = "SPEC-A1.1", BorangKey = "A1", RevitElementId = 123456, Status = IssueStatus.Open };
            var cells = IssueExportRows.Cells(i);
            Assert.Equal(IssueExportRows.Header.Length, cells.Length);
            Assert.Equal("123456", cells[7]);
            Assert.Equal("A1.1", cells[8]);
        }

        [Fact]
        public void Element_id_cell_lists_all_elements_semicolon_separated()
        {
            var i = new IssueVm { RevitElementId = 11, RevitElementIds = new List<long> { 11, 22, 33 } };
            Assert.Equal("11;22;33", IssueExportRows.ElementIdsCell(i));
        }

        [Fact]
        public void Element_id_cell_holds_all_300_ids_with_no_marker()
        {
            var ids = Enumerable.Range(0, 300).Select(n => 1000000L + n).ToList();
            var i = new IssueVm { RevitElementId = ids[0], RevitElementIds = ids };
            foreach (var lang in new[] { "ms", "en" })
                In(lang, () =>
                {
                    var cell = IssueExportRows.ElementIdsCell(i);
                    Assert.Equal(ids, cell.Split(';').Select(long.Parse).ToList());
                    Assert.DoesNotContain("+", cell);
                });
        }

        [Fact]
        public void Element_id_cell_is_cut_only_over_the_excel_limit_with_a_marker_in_both_languages()
        {
            var ids = Enumerable.Range(0, 5000).Select(n => 1000000L + n).ToList();   // ~40,000 chars
            var i = new IssueVm { RevitElementId = ids[0], RevitElementIds = ids };
            foreach (var (lang, word) in new[] { ("ms", "lagi"), ("en", "more") })
                In(lang, () =>
                {
                    var cell = IssueExportRows.ElementIdsCell(i);
                    Assert.True(cell.Length <= IssueExportRows.MaxCellChars);
                    var cut = cell.LastIndexOf(" +", System.StringComparison.Ordinal);
                    var listed = cell.Substring(0, cut).Split(';').Select(long.Parse).ToList();
                    Assert.Equal(ids.Take(listed.Count).ToList(), listed);
                    Assert.Equal($" +{ids.Count - listed.Count} {word}", cell.Substring(cut));
                });
        }

        [Fact]
        public void Element_id_cell_at_exactly_the_limit_is_not_cut_and_one_over_is()
        {
            var i = new IssueVm { RevitElementIds = new List<long> { 1111111, 2222222, 3333333 } };   // 23 chars
            Assert.Equal("1111111;2222222;3333333", IssueExportRows.ElementIdsCell(i, 23));            // at the limit: whole
            In("en", () => Assert.Equal("1111111 +2 more", IssueExportRows.ElementIdsCell(i, 22)));       // one over: cut
        }


        [Fact]
        public void Element_id_cell_is_blank_without_an_element_and_falls_back_to_the_single_id()
        {
            Assert.Equal("", IssueExportRows.ElementIdsCell(new IssueVm { RevitElementId = 0 }));
            Assert.Equal("", IssueExportRows.ElementIdsCell(new IssueVm { RevitElementId = -1 }));
            Assert.Equal("77", IssueExportRows.ElementIdsCell(new IssueVm { RevitElementId = 77 }));
        }

        [Fact]
        public void Actual_and_Reviewed_cells_follow_the_language()
        {
            var i = new IssueVm { Actual = "Tiada material", ActualEn = "No material" };
            In("ms", () => Assert.Equal("Tiada material", IssueExportRows.Cells(i)[6]));
            In("en", () => Assert.Equal("No material", IssueExportRows.Cells(i)[6]));
            var noEn = new IssueVm { Actual = "kosong" };
            In("en", () => Assert.Equal("kosong", IssueExportRows.Cells(noEn)[6]));
        }

        [Theory]
        [InlineData("SPEC-C5.1.1", "C5.1.1")]
        [InlineData("SPEC-A1.1-3f9c0a1b2c3d", "A1.1")]          // StableId shape: prefix, check id, '-' + 12 hex
        [InlineData("SPEC-Dd.1-3F9C0A1B2C3D", "Dd.1")]
        [InlineData("  spec-a1.2  ", "a1.2")]                    // prefix is case-insensitive; Trim
        [InlineData("BIM010-A6", "A6")]
        [InlineData("BIM010-D10", "D10")]
        [InlineData("SPEC-X-12", "X-12")]                        // a short '-' tail is not a hash
        [InlineData("backend_rule_7", "backend_rule_7")]         // backend id: as is
        [InlineData("odd id", "odd id")]
        [InlineData("SPEC-", "SPEC-")]
        [InlineData("", "")]
        public void Rule_code_drops_the_prefix_and_hash(string checkId, string code) =>
            Assert.Equal(code, IssueExportRows.RuleCodeOf(new IssueVm { CheckId = checkId }));

        [Fact]
        public void Export_writes_exactly_the_issues_it_is_given_no_hidden_drops()
        {
            // Legacy-vs-spec duplicates are removed upstream (the scan merge), so the export
            // does not filter by Borang row: a BIM010- issue beside a SPEC- one on the same row is written.
            var spec = new IssueVm { Id = "s1", CheckId = "SPEC-A1.1", BorangKey = "A1", Status = IssueStatus.Open };
            var other = new IssueVm { Id = "l1", CheckId = "BIM010-A1", BorangKey = "A1", Status = IssueStatus.Open };
            var all = new[] { spec, other };
            Assert.Equal(new[] { "s1", "l1" }, IssueExportRows.Select(all, true, true).Select(x => x.Id).ToArray());
            Assert.Equal(2, IssueExportRows.DefaultCount(all));
        }

        [Fact]
        public void Nine_columns_and_csv_quoting()
        {
            Assert.Equal(9, IssueExportRows.Header.Length);
            Assert.Equal(9, IssueExportRows.Cells(Sample()[0]).Length);
            Assert.Equal("\"a,\"\"b\"\"\"", IssueExportRows.CsvCell("a,\"b\""));
            Assert.Equal("", IssueExportRows.CsvCell(null));
        }
    }
}
