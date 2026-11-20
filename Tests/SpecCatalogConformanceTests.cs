using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Row-by-row conformance of SpecCatalog / SpecReport to the spec workbook
    /// (JKR-Model-Audit-Spec.xlsx, sheet "Peraturan comply"). data/spec_rows.json is
    /// GENERATED from the workbook's source (see data/README-spec_rows.md) and committed;
    /// it is never regenerated at test time. When the Excel and the code drift apart these
    /// fail and name the rows. WPF-free: runs on the Mac harness.
    /// </summary>
    public class SpecCatalogConformanceTests
    {
        private sealed class Row { public string Id, Class, ComplyText, NotComplyText; }

        private static List<Row> Load() =>
            JArray.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "spec_rows.json")))
                .Select(j => new Row
                {
                    Id = (string)j["id"], Class = (string)j["class"],
                    ComplyText = (string)j["comply_text"], NotComplyText = (string)j["not_comply_text"],
                }).ToList();

        public static IEnumerable<object[]> RowIds() => Load().Select(r => new object[] { r.Id });

        private static SpecCellClass ClassOf(string s) => (SpecCellClass)Enum.Parse(typeof(SpecCellClass), s);

        [Fact]
        public void Fixture_has_61_rows_39_Ai_8_Human_14_List_and_unique_ids()
        {
            var rows = Load();
            Assert.Equal(61, rows.Count);
            Assert.Equal(61, rows.Select(r => r.Id).Distinct().Count());
            Assert.Equal(39, rows.Count(r => r.Class == "Ai"));
            Assert.Equal(8, rows.Count(r => r.Class == "Human"));
            Assert.Equal(14, rows.Count(r => r.Class == "List"));
        }

        [Fact]
        public void Catalog_ids_and_classes_match_the_spec_rows_exactly()
        {
            var spec = Load().ToDictionary(r => r.Id, r => r.Class);
            var cat = SpecCatalog.Checks.ToDictionary(c => c.Id, c => c.Class.ToString());

            var drift = new List<string>();
            drift.AddRange(spec.Keys.Except(cat.Keys).OrderBy(x => x).Select(id => $"in spec, missing from SpecCatalog: {id} ({spec[id]})"));
            drift.AddRange(cat.Keys.Except(spec.Keys).OrderBy(x => x).Select(id => $"in SpecCatalog, not in spec: {id} ({cat[id]})"));
            drift.AddRange(spec.Keys.Intersect(cat.Keys).Where(id => spec[id] != cat[id]).OrderBy(x => x)
                .Select(id => $"class differs for {id}: spec {spec[id]}, SpecCatalog {cat[id]}"));

            Assert.True(drift.Count == 0, "SpecCatalog and spec_rows.json drifted:\n" + string.Join("\n", drift));
        }

        [Theory]
        [MemberData(nameof(RowIds))]
        public void Row_state_without_any_result_is_NotChecked_or_NeedsPerson(string id)
        {
            var row = Load().Single(r => r.Id == id);
            var res = SpecReport.Build(null).For(id);
            Assert.NotNull(res);
            // Human: always NeedsPerson. Ai and List: nothing ran, nothing to say.
            Assert.Equal(row.Class == "Human" ? SpecState.NeedsPerson : SpecState.NotChecked, res.State);
        }

        [Theory]
        [MemberData(nameof(RowIds))]
        public void Row_state_on_the_passing_snapshot_follows_the_class(string id)
        {
            var row = Load().Single(r => r.Id == id);
            var report = SpecReport.Build(new[] { Snapshot(id, "pass") });
            var res = report.For(id);
            Assert.NotNull(res);
            Assert.Equal(ClassOf(row.Class), res.Check.Class);
            var expected = row.Class == "Ai" ? SpecState.Pass : row.Class == "Human" ? SpecState.NeedsPerson : SpecState.Listed;
            Assert.Equal(expected, res.State);
        }

        [Theory]
        [MemberData(nameof(RowIds))]
        public void Row_state_on_the_failing_snapshot_follows_the_class(string id)
        {
            var row = Load().Single(r => r.Id == id);
            var report = SpecReport.Build(new[] { Snapshot(id, "fail") });
            var res = report.For(id);
            Assert.NotNull(res);
            var expected = row.Class == "Ai" ? SpecState.Fail : row.Class == "Human" ? SpecState.NeedsPerson : SpecState.Listed;
            Assert.Equal(expected, res.State);
        }

        // One local-evaluator result that answers exactly this check with a full verdict rule.
        private static SpecResult Snapshot(string id, string status) => new SpecResult
        {
            Source = SpecReport.Model, CheckId = id, Status = status, Coverage = SpecCoverage.Full,
            Rule = "conformance " + id, IssueId = status == "fail" ? "SPEC-" + id + "-x" : "",
        };
    }
}
