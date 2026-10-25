#nullable enable
// W2: envelope shapes of the read tools (model_overview here; find_elements,
// inspect and look in later tasks). Every envelope must pass the §2.1
// invariants, keep long lists to the limit with a count and a way to narrow,
// and say what was and was not searched.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FindResultShapeTests
    {
        private static void AssertValid(Envelope e) =>
            Assert.True(EnvelopeInvariants.Violations(e).Count == 0, string.Join("; ", EnvelopeInvariants.Violations(e)));

        private static OverviewSection Rows(string name, int n, System.Func<int, Dictionary<string, object?>> row)
        {
            var s = new OverviewSection { Name = name };
            for (var i = 1; i <= n; i++) s.Rows.Add(row(i));
            return s;
        }

        private static OverviewSections.Args OverviewArgs(int limit, string? category, params string[] sections) =>
            new OverviewSections.Args { Sections = sections.ToList(), Limit = limit, Category = category };

        // ── model_overview ────────────────────────────────────────────────

        [Fact]
        public void Overview_CutsEachSectionAtLimit_WithCountAndNarrowHint()
        {
            var views = Rows("views", 120, i => new Dictionary<string, object?>
                { ["name"] = "View " + i, ["type"] = i % 2 == 0 ? "FloorPlan" : "Section" });
            views.NarrowKey = "type";
            views.NarrowHint = "a view type, e.g. FloorPlan";
            var levels = Rows("levels", 3, i => new Dictionary<string, object?> { ["name"] = "Level " + i });

            var env = OverviewSections.Build("Pejabat", new[] { levels, views }, OverviewArgs(50, null, "levels", "views"));
            AssertValid(env);
            Assert.True(env.Ok);
            Assert.Equal(EnvelopePhase.Read, env.Phase);

            var data = env.Data!.Value;
            Assert.Equal(3, data.GetProperty("levels").GetProperty("rows").GetArrayLength());
            Assert.False(data.GetProperty("levels").TryGetProperty("more", out _));

            var v = data.GetProperty("views");
            Assert.Equal(50, v.GetProperty("rows").GetArrayLength());
            Assert.Equal(120, v.GetProperty("total").GetInt32());
            Assert.Equal(70, v.GetProperty("more").GetInt32());
            Assert.Contains("70 more; narrow with category=", v.GetProperty("hint").GetString());
            Assert.Contains("views: 70 more", env.Next);
            Assert.Contains("120 views", env.Summary);
            Assert.Contains("3 levels", env.Summary);
        }

        [Fact]
        public void Overview_NarrowsByCategoryText()
        {
            var views = Rows("views", 10, i => new Dictionary<string, object?>
                { ["name"] = "View " + i, ["type"] = i <= 4 ? "FloorPlan" : "CeilingPlan" });
            views.NarrowKey = "type";
            var env = OverviewSections.Build("Pejabat", new[] { views }, OverviewArgs(50, "floor plan", "views"));
            AssertValid(env);
            var v = env.Data!.Value.GetProperty("views");
            Assert.Equal(4, v.GetProperty("total").GetInt32());
            Assert.Equal("floor plan", v.GetProperty("narrowed_by").GetString());
        }

        [Fact]
        public void Overview_ProjectSectionCarriesFieldsNotRows()
        {
            var project = new OverviewSection { Name = "project", IsList = false };
            project.Extra["name"] = "Pejabat Daerah";
            project.Extra["phases"] = new List<string> { "Existing", "New Construction" };
            var env = OverviewSections.Build("Pejabat", new[] { project }, OverviewArgs(50, null, "project"));
            AssertValid(env);
            var p = env.Data!.Value.GetProperty("project");
            Assert.Equal("Pejabat Daerah", p.GetProperty("name").GetString());
            Assert.False(p.TryGetProperty("rows", out _));
        }

        [Fact]
        public void Overview_UsesTrueTotalWhenRowsWereCapped()
        {
            var warnings = Rows("warnings", 5, i => new Dictionary<string, object?> { ["text"] = "W" + i, ["count"] = 10 });
            warnings.Total = 812;
            warnings.TotalLabel = "warnings";
            var env = OverviewSections.Build("Pejabat", new[] { warnings }, OverviewArgs(50, null, "warnings"));
            AssertValid(env);
            Assert.Equal(812, env.Data!.Value.GetProperty("warnings").GetProperty("total").GetInt32());
            Assert.Contains("812 warnings", env.Summary);
        }

        [Fact]
        public void Overview_SummaryAndNextStayWithinCaps()
        {
            var sections = OverviewSections.All
                .Where(n => n != "project")
                .Select(n =>
                {
                    var s = Rows(n, 500, i => new Dictionary<string, object?> { ["name"] = n + " " + i });
                    s.NarrowHint = "a very long description of how to narrow this particular section for the model";
                    return s;
                })
                .ToList();
            var env = OverviewSections.Build(new string('x', 400), sections,
                OverviewArgs(10, null, OverviewSections.All.Where(n => n != "project").ToArray()));
            AssertValid(env);
            Assert.True(env.Summary.Length <= EnvelopeLimits.SummaryMaxChars);
            Assert.True(env.Next!.Length <= EnvelopeLimits.NextMaxChars);
        }
    }
}
