// Coverage guard: which BIM 010 rows resolve to a deterministic checker.
//
// fill_audit's honesty contract is "unmatched row → not_verifiable, never
// guess". That makes the SET of unmatched rows the tool's accuracy budget, and
// today it is exactly the rows no rule can judge: two subjective A rows and
// the four Section C sub-headings that carry no criterion. A keyword edit that
// silently drops a real row into not_verifiable (or makes a subjective row
// "match" something) must fail here, not be discovered on a filled form.
//
// Only AuditMatching is exercised — pure keyword scoring over
// Description + GuidelineRef. No Document, no Evaluate.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using Xunit;

namespace Tests
{
    public class AuditCoverageTests
    {
        private const string FormPdf = "bim010_model_audit_form.pdf";

        private static string Fixture(string name) =>
            Path.Combine(AppContext.BaseDirectory, "data", name);

        private static List<AuditFormRow> Parsed() => AuditFormParser.Parse(Fixture(FormPdf));

        /// <summary>Rows that are allowed to be not_verifiable, and why. Adding
        /// to this list is a deliberate accuracy decision — say why.</summary>
        private static readonly Dictionary<string, string> AllowedUnmatched = new()
        {
            ["A 1"] = "subjective: 'in accordance to specified architectural design'",
            ["A 3"] = "subjective: 'structured according to rules that are easily managed'",
            ["C 1.0"] = "sub-heading 'Views (Architectural)' — no criterion",
            ["C 2.0"] = "sub-heading 'Legends' — no criterion",
            ["C 4.0"] = "sub-heading 'Sheets' — no criterion",
            ["C 5.0"] = "sub-heading 'Link Files' — no criterion",
        };

        /// <summary>Golden row → checker mapping for every row that must match.
        /// Section D rows all route to family_category via the category map.</summary>
        private static readonly Dictionary<string, string> ExpectedChecker = new()
        {
            ["A 2"] = "file_naming",
            ["A 4"] = "base_point",
            ["A 5"] = "grids_levels",
            ["A 6"] = "rooms_department",
            ["A 7"] = "materials_assigned",
            ["B 1"] = "project_info",
            ["C 1.1"] = "views_template",
            ["C 1.2"] = "views_wip",
            ["C 1.3"] = "views_pbt",
            ["C 1.4"] = "views_bomba",
            ["C 1.5"] = "views_dokumen",
            ["C 1.6"] = "area_plans",
            ["C 2.1"] = "legends",
            // "Schedules/Quantities" heading carries the 'quantities' keyword, so
            // it resolves like 3.1 — recorded, not endorsed.
            ["C 3.0"] = "schedules_required",
            ["C 3.1"] = "schedules_required",
            ["C 4.1"] = "sheets_contents",
            ["C 4.2"] = "titleblock_jkr",
            ["C 5.1"] = "links_current",
            // E 1.0 matches but its evaluator returns not_verifiable at run time
            // because the heading names no view set (BOMBA/PBT) — honest by design.
            ["E 1.0"] = "view_template_applied",
            ["E 1.1"] = "view_template_applied",
            ["E 1.2"] = "view_template_applied",
        };

        private static string Key(AuditFormRow r) => r.Section + " " + r.RowRef;

        [Fact]
        public void UnmatchedRowsAreExactlyTheKnownSubjectiveAndHeadingRows()
        {
            var rows = Parsed();
            Assert.NotEmpty(rows);

            var unmatched = rows.Where(r => AuditMatching.Match(r).checkerId == null).Select(Key).ToList();

            var unexpected = unmatched.Except(AllowedUnmatched.Keys).ToList();
            var nowMatched = AllowedUnmatched.Keys.Except(unmatched).ToList();

            Assert.True(unexpected.Count == 0,
                "rows fell through to not_verifiable that a checker used to cover:\n  "
                + string.Join("\n  ", unexpected.Select(k =>
                    k + ": " + rows.First(r => Key(r) == k).Description)));
            Assert.True(nowMatched.Count == 0,
                "rows that must stay manual now match a checker (a fabricated verdict):\n  "
                + string.Join("\n  ", nowMatched.Select(k =>
                    $"{k} → {AuditMatching.Match(rows.First(r => Key(r) == k)).checkerId}"
                    + $" ({AllowedUnmatched[k]})")));
        }

        [Fact]
        public void EveryCoveredRowResolvesToItsExpectedChecker()
        {
            var rows = Parsed();
            var wrong = new List<string>();
            foreach (var row in rows)
            {
                var key = Key(row);
                if (AllowedUnmatched.ContainsKey(key)) continue;
                var (checkerId, category) = AuditMatching.Match(row);
                string actual = checkerId ?? "<none>";

                string expected;
                if (row.Section == "D")
                {
                    expected = AuditMatching.FamilyCategoryId;
                    if (checkerId != null && string.IsNullOrEmpty(category))
                        wrong.Add($"{key}: Section D row without a category label");
                }
                else if (!ExpectedChecker.TryGetValue(key, out expected!))
                {
                    wrong.Add($"{key}: row not in the golden mapping (add it, or to AllowedUnmatched)");
                    continue;
                }
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    wrong.Add($"{key}: expected {expected}, got {actual} — {row.Description}");
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        [Fact]
        public void GoldenMappingNamesOnlyRowsThatExistOnTheForm()
        {
            var keys = Parsed().Select(Key).ToHashSet(StringComparer.Ordinal);
            var stale = ExpectedChecker.Keys.Concat(AllowedUnmatched.Keys).Where(k => !keys.Contains(k)).ToList();
            Assert.True(stale.Count == 0, "mapping names rows the form does not have: " + string.Join(", ", stale));
        }

        private static AuditFormRow Row(string section, string description, string guidelineRef = "") =>
            new() { Section = section, RowRef = "x", Description = description, GuidelineRef = guidelineRef };

        [Theory]
        [InlineData("BOMBA Submission", "view_template_applied")]   // E rows: not views_bomba
        [InlineData("PBT Submission", "view_template_applied")]
        [InlineData("BOMBA views are used for BOMBA submission purposes.", "views_bomba")] // tie → earlier entry
        [InlineData("Views (Architectural)", null)]                   // heading: 1 group < MinGroups
        [InlineData("Views created based from template", "views_template")]
        [InlineData("Views created based from PWD", null)]            // views_template needs all 3 groups
        [InlineData("Rooms organised by grid and levels", "grids_levels")]          // no 'department' → not a rooms row
        [InlineData("Rooms by department, organised on grid and levels", "rooms_department")] // 2 vs 2 tie → rooms first
        public void MatchesByKeywordGroupsWithOrderedTieBreak(string description, string? expected)
        {
            Assert.Equal(expected, AuditMatching.Match(Row("C", description)).checkerId);
        }

        [Fact]
        public void GuidelineRefTakesPartInMatching()
        {
            // Description alone has one group; the reference cell supplies the other.
            Assert.Null(AuditMatching.Match(Row("A", "All materials")).checkerId);
            Assert.Equal("materials_assigned",
                AuditMatching.Match(Row("A", "All materials", "model elements")).checkerId);
        }

        [Fact]
        public void SectionDUsesFirstCategoryLabelInOrder()
        {
            Assert.Equal("furniture systems", AuditMatching.Match(Row("D", "Furniture Systems")).category);
            Assert.Equal("furniture", AuditMatching.Match(Row("D", "Furniture")).category);
            Assert.Equal("structural column", AuditMatching.Match(Row("D", "Structural Column")).category);
            Assert.Equal("columns", AuditMatching.Match(Row("D", "Columns")).category);
            Assert.Equal((null, null), AuditMatching.Match(Row("D", "Signage")));
        }

        [Fact]
        public void EverySectionDRowMapsToACategory()
        {
            var rows = Parsed().Where(r => r.Section == "D").ToList();
            Assert.Equal(23, rows.Count);
            var missing = rows.Where(r => AuditMatching.Match(r).checkerId == null).Select(r => Key(r) + " " + r.Description).ToList();
            Assert.True(missing.Count == 0, "Section D rows with no CategoryLabels entry: " + string.Join(", ", missing));
        }
    }
}
