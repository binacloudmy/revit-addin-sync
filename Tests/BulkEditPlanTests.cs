// Bulk edit planners — Revit-free (bina-ai R2 Task 22, bulk parameter/type pack).
//
// A filter-scoped write must show the exact per-element diff BEFORE any
// transaction, account for every matched element (changed / unchanged /
// read-only / grouped), and after the commit re-read the values it wrote so
// the same call reports whether they actually took. These planners hold that
// logic so it can be tested without Revit.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.BulkEdit;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class BulkEditPlanTests
    {
        private static List<ParamRow> Rows(params (long id, string name, string? current, bool readOnly, bool grouped)[] rows) =>
            rows.Select(r => new ParamRow { Id = r.id, Name = r.name, Current = r.current, ReadOnly = r.readOnly, Grouped = r.grouped }).ToList();

        [Fact]
        public void ParamPlan_DiffsOnlyElementsWhoseValueChanges_AndAccountsForTheRest()
        {
            var rows = Rows((1, "D1", "", false, false), (2, "D2", "1HR", false, false), (3, "D3", "2HR", false, false),
                            (4, "D4", "", true, false), (5, "D5", "", false, true));
            var plan = ParamPlan.Build(rows, "2HR", onlyEmpty: false, includeGrouped: false);
            Assert.Equal(new[] { 1L, 2L }, plan.Changes.Select(c => c.Id));
            Assert.Equal(("", "2HR"), (plan.Changes[0].From, plan.Changes[0].To));
            Assert.Equal(1, plan.Unchanged);
            Assert.Equal(1, plan.ReadOnly);
            Assert.Equal(1, plan.GroupedSkipped);
            Assert.Equal(5, plan.Matched);
            Assert.Equal(plan.Matched, plan.Changes.Count + plan.Unchanged + plan.ReadOnly + plan.GroupedSkipped);
        }

        [Fact]
        public void ParamPlan_OnlyEmpty_LeavesFilledValuesAlone()
        {
            var rows = Rows((1, "D1", "", false, false), (2, "D2", "1HR", false, false));
            var plan = ParamPlan.Build(rows, "2HR", onlyEmpty: true, includeGrouped: false);
            Assert.Single(plan.Changes);
            Assert.Equal(1L, plan.Changes[0].Id);
            Assert.Equal(1, plan.Unchanged);
        }

        [Fact]
        public void ParamPlan_IncludeGrouped_WritesGroupMembers()
        {
            var rows = Rows((5, "D5", "", false, true));
            Assert.Empty(ParamPlan.Build(rows, "x", false, includeGrouped: false).Changes);
            Assert.Single(ParamPlan.Build(rows, "x", false, includeGrouped: true).Changes);
        }

        [Fact]
        public void Verify_ReportsMismatchesWithExpectedAndActual()
        {
            var expected = new Dictionary<long, string> { [1] = "2HR", [2] = "2HR" };
            var actual = new Dictionary<long, string?> { [1] = "2HR", [2] = "1HR" };
            var v = WriteVerification.Verify(expected, id => actual.TryGetValue(id, out var s) ? s : null);
            Assert.Equal(2, v["checked"]);
            Assert.Equal(1, v["matches"]);
            var mm = Assert.Single((List<object>)v["mismatches"]!);
            var d = (Dictionary<string, object?>)mm;
            Assert.Equal(2L, d["id"]); Assert.Equal("2HR", d["expected"]); Assert.Equal("1HR", d["actual"]);
        }

        [Fact]
        public void TypeSwapPlan_SkipsElementsAlreadyOfTheTargetType()
        {
            var rows = new List<TypeRow>
            {
                new() { Id = 1, FromType = "JKR-P100" }, new() { Id = 2, FromType = "JKR-P150" }, new() { Id = 3, FromType = "JKR-P100" },
            };
            var plan = TypeSwapPlan.Build(rows, "JKR-P150");
            Assert.Equal(new[] { 1L, 3L }, plan.Changes.Select(c => c.Id));
            Assert.Equal(1, plan.Unchanged);
            Assert.Equal(3, plan.Matched);
        }

        [Fact]
        public void Preview_IsCappedButCountsAreExact()
        {
            var rows = Enumerable.Range(1, 300).Select(i => new ParamRow { Id = i, Name = $"E{i}", Current = "" }).ToList();
            var preview = ParamPlan.Build(rows, "v", false, false).ToPreview(cap: 200);
            Assert.Equal(300, preview["would_set"]);
            Assert.Equal(300, preview["matched"]);
            Assert.Equal(200, ((IEnumerable<object>)preview["preview"]!).Count());
            Assert.Equal(true, preview["preview_truncated"]);
        }

        [Fact]
        public void ParamPlan_MissingParameter_IsItsOwnBucket_NotReadOnly()
        {
            var rows = new[]
            {
                new ParamRow { Id = 1, Name = "a", Current = "", ReadOnly = false },
                new ParamRow { Id = 2, Name = "b", Current = null, Missing = true },
                new ParamRow { Id = 3, Name = "c", Current = "1HR", ReadOnly = true },
            };
            var plan = ParamPlan.Build(rows, "2HR", onlyEmpty: false, includeGrouped: false);
            Assert.Single(plan.Changes); Assert.Equal(1, plan.Missing); Assert.Equal(1, plan.ReadOnly);
            Assert.Equal(plan.Matched, plan.Changes.Count + plan.Unchanged + plan.ReadOnly + plan.GroupedSkipped + plan.Missing);
            Assert.Equal(1, plan.ToPreview()["missing"]);
        }
    }
}
