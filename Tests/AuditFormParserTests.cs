// AuditFormParser against the real BIM 010 Model Audit Form.
//
// Why a golden file and not a snapshot: the parser's job is to reproduce what a
// human reads off the printed table, so the expectations in
// data/bim010_expected_rows.tsv were verified page by page against the PDF, not
// generated from the parser's output. A regression here means a description
// moved to the wrong row — the failure mode that made the tool's remarks quote
// the wrong checklist item.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using Xunit;

namespace Tests
{
    public class AuditFormParserTests
    {
        private const string FormPdf = "bim010_model_audit_form.pdf";
        private const string GoldenTsv = "bim010_expected_rows.tsv";

        private sealed record ExpectedRow(string Section, string RowRef, string Description);

        private static string Fixture(string name) =>
            Path.Combine(AppContext.BaseDirectory, "data", name);

        private static List<ExpectedRow> Golden()
        {
            var rows = new List<ExpectedRow>();
            foreach (var line in File.ReadAllLines(Fixture(GoldenTsv)))
            {
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = line.Split('\t');
                Assert.Equal(3, parts.Length);   // section \t row_ref \t description
                rows.Add(new ExpectedRow(parts[0], parts[1], parts[2]));
            }
            return rows;
        }

        private static List<AuditFormRow> Parsed() => AuditFormParser.Parse(Fixture(FormPdf));

        [Fact]
        public void ParsesEveryRowOfTheFormInOrder()
        {
            var expected = Golden();
            var rows = Parsed();

            Assert.Equal(expected.Count, rows.Count);
            Assert.Equal(
                expected.Select(e => e.Section + " " + e.RowRef).ToList(),
                rows.Select(r => r.Section + " " + r.RowRef).ToList());
        }

        [Fact]
        public void EachRowCarriesItsOwnDescription()
        {
            var expected = Golden();
            var rows = Parsed();
            Assert.Equal(expected.Count, rows.Count);

            var wrong = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (string.Equals(expected[i].Description, rows[i].Description, StringComparison.Ordinal))
                    continue;
                wrong.Add($"row {expected[i].Section} {expected[i].RowRef}:\n"
                          + $"  expected: {expected[i].Description}\n"
                          + $"  actual:   {rows[i].Description}");
            }
            Assert.True(wrong.Count == 0,
                $"{wrong.Count}/{rows.Count} descriptions do not match the form:\n"
                + string.Join("\n", wrong));
        }

        [Fact]
        public void RowRefsAreUniqueWithinASection()
        {
            foreach (var group in Parsed().GroupBy(r => r.Section))
            {
                var dupes = group.GroupBy(r => r.RowRef).Where(g => g.Count() > 1)
                                 .Select(g => g.Key).ToList();
                Assert.True(dupes.Count == 0,
                    $"section {group.Key} has repeated row refs: {string.Join(", ", dupes)}");
            }
        }

        [Fact]
        public void SectionReferencesAreNeverInvented()
        {
            // Item 6: a ref is either printed on the row, inherited from the
            // section's single printed ref, or absent. Nothing else.
            foreach (var row in Parsed())
            {
                if (row.GuidelineRef.Length == 0)
                {
                    Assert.Equal("", row.ReferenceSource);
                    continue;
                }
                Assert.Contains(row.ReferenceSource, new[] { "form", "form_sibling" });
            }
        }

        [Fact]
        public void InheritedReferencesMatchTheirSectionsPrintedRef()
        {
            foreach (var group in Parsed().GroupBy(r => r.Section))
            {
                var inherited = group.Where(r => r.ReferenceSource == "form_sibling")
                                     .Select(r => r.GuidelineRef).Distinct().ToList();
                if (inherited.Count == 0) continue;
                var printed = group.Where(r => r.ReferenceSource == "form")
                                   .Select(r => r.GuidelineRef).Distinct().ToList();
                Assert.Single(printed);
                Assert.Equal(printed, inherited);
            }
        }
    }
}
