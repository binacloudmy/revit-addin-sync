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
        // ── find_elements ─────────────────────────────────────────────────

        private static FindPlan FindPlanOf(string json)
        {
            Assert.True(QueryParser.TryParse(JsonDocument.Parse(json).RootElement.Clone(), out var plan, out var error), error);
            return plan;
        }

        private static FoundRow Row(long id, string type, string level, long? link = null, string category = "Ceilings") =>
            new FoundRow { Ref = new ElementRef(id, link), Category = category, Type = type, Level = level, GroupKey = type };

        [Fact]
        public void FindResult_ZeroMatches_ListsNotSearchedAndNearMatches()
        {
            // Review Focus 2: "none found" while a link is unloaded or a workset is closed.
            var plan = FindPlanOf("{\"queries\":[{\"text\":\"pintu kalis api\",\"level\":\"Level 3\"}]}");
            var outcome = new FindOutcome
            {
                Sources = { "host" },
                Categories = { "Doors" },
                NotSearched = { "link: ARC_Core (unloaded)", "workset: Doors-L3 (closed)" },
                NearMatches = { "Door - Fire Rated 60 min: 14 on Level 2", "Door - Single Flush 900: 22 on Level 3" },
            };
            outcome.Queries.Add(new QueryOutcome
            {
                Count = 0,
                Filter = "category = Doors AND level = 'Level 3' AND text ~ 'kalis api' (fire rated)",
                Subject = "Doors on Level 3 matching 'kalis api' (fire rated)",
            });

            var env = FindResultBuilder.Build(plan, outcome);
            AssertValid(env);
            Assert.True(env.Ok);
            Assert.Equal(EnvelopeStatus.Done, env.Status);
            Assert.Equal(0, env.Count);
            Assert.Null(env.Handle);

            Assert.NotNull(env.Searched);
            Assert.Equal(new[] { "host" }, env.Searched!.Sources);
            Assert.Equal(new[] { "Doors" }, env.Searched.Categories);
            Assert.Equal(new[] { "link: ARC_Core (unloaded)", "workset: Doors-L3 (closed)" }, env.Searched.NotSearched);
            Assert.Contains("kalis api", env.Searched.Filter);

            var data = env.Data!.Value;
            Assert.Equal(0, data.GetProperty("rows").GetArrayLength());
            Assert.Equal(2, data.GetProperty("near_matches").GetArrayLength());

            Assert.StartsWith("No Doors on Level 3", env.Summary);
            Assert.Contains("not searched", env.Summary, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ARC_Core", env.Summary);
            Assert.Contains("ARC_Core", env.Next);
            Assert.Contains("near matches", env.Next);
        }

        [Fact]
        public void FindResult_ZeroMatches_WithEverythingSearched_StillSaysWhatWasSearched()
        {
            var plan = FindPlanOf("{\"queries\":[{\"category\":\"Doors\"}]}");
            var outcome = new FindOutcome { Sources = { "host" }, Categories = { "Doors" } };
            outcome.Queries.Add(new QueryOutcome { Count = 0, Filter = "category = Doors", Subject = "Doors" });
            var env = FindResultBuilder.Build(plan, outcome);
            AssertValid(env);
            Assert.Empty(env.Searched!.NotSearched);
            Assert.Equal("No Doors in the host model.", env.Summary);
            Assert.Equal(0, env.Data!.Value.GetProperty("near_matches").GetArrayLength());
        }

        [Fact]
        public void FindResult_CutsRowsAtLimit_CountsAllAndPointsAtHandle()
        {
            var plan = FindPlanOf("{\"queries\":[{\"category\":\"Ceilings\",\"level\":\"Level 2\"}],\"limit\":3}");
            var outcome = new FindOutcome { Sources = { "host" }, Categories = { "Ceilings" }, Handle = "h_7f3a91c2",
                NotSearched = { "link: STR_v3 (unloaded)" } };
            for (var i = 0; i < 28; i++) outcome.Rows.Add(Row(312000 + i, "CL-02A 600x600 Grid", "Level 2"));
            for (var i = 0; i < 6; i++) outcome.Rows.Add(Row(313000 + i, "CL-20 Plasterboard", "Level 2"));
            outcome.Queries.Add(new QueryOutcome { Count = 34, Filter = "category = Ceilings AND level = 'Level 2'", Subject = "Ceilings on Level 2" });

            var env = FindResultBuilder.Build(plan, outcome);
            AssertValid(env);
            Assert.Equal(34, env.Count);
            Assert.Equal("h_7f3a91c2", env.Handle);
            Assert.Equal(3, env.Data!.Value.GetProperty("rows").GetArrayLength());
            Assert.Equal(312000, env.Data.Value.GetProperty("rows")[0].GetProperty("ref").GetProperty("id").GetInt64());
            Assert.Contains("3 of 34", env.Next);
            Assert.Contains("h_7f3a91c2", env.Next);
            Assert.StartsWith("34 Ceilings on Level 2 in the host model: 28 CL-02A 600x600 Grid, 6 CL-20 Plasterboard.", env.Summary);
            Assert.Contains("STR_v3", env.Summary);
        }

        [Fact]
        public void FindResult_GroupsCountAllMatchesNotJustShownRows()
        {
            var plan = FindPlanOf("{\"queries\":[{\"category\":\"Doors\"}],\"group_by\":\"level\",\"limit\":1}");
            var outcome = new FindOutcome { Sources = { "host" }, Categories = { "Doors" }, Handle = "h_00000001" };
            for (var i = 0; i < 5; i++) outcome.Rows.Add(new FoundRow { Ref = new ElementRef(i + 1, null), Category = "Doors", Level = "Level 1", GroupKey = "Level 1" });
            for (var i = 0; i < 9; i++) outcome.Rows.Add(new FoundRow { Ref = new ElementRef(i + 100, null), Category = "Doors", Level = "Level 2", GroupKey = "Level 2" });
            outcome.Queries.Add(new QueryOutcome { Count = 14, Filter = "category = Doors", Subject = "Doors" });

            var env = FindResultBuilder.Build(plan, outcome);
            AssertValid(env);
            var groups = env.Data!.Value.GetProperty("groups");
            Assert.Equal(2, groups.GetArrayLength());
            Assert.Equal("Level 2", groups[0].GetProperty("key").GetString());
            Assert.Equal(9, groups[0].GetProperty("count").GetInt32());
            Assert.Equal(5, groups[1].GetProperty("count").GetInt32());
            Assert.Equal("level", env.Data.Value.GetProperty("group_by").GetString());
            Assert.Contains("9 Level 2, 5 Level 1", env.Summary);
        }

        [Fact]
        public void FindResult_MultipleQueries_ReportPerQueryCountsAndHandles()
        {
            var plan = FindPlanOf("{\"queries\":[{\"category\":\"Doors\"},{\"category\":\"Windows\"}]}");
            var outcome = new FindOutcome { Sources = { "host" }, Categories = { "Doors", "Windows" }, Handle = "h_00000003" };
            outcome.Rows.Add(new FoundRow { Ref = new ElementRef(1, null), Category = "Doors", Queries = { 0 } });
            outcome.Rows.Add(new FoundRow { Ref = new ElementRef(2, null), Category = "Doors", Queries = { 0 } });
            outcome.Queries.Add(new QueryOutcome { Count = 2, Filter = "category = Doors", Subject = "Doors", Handle = "h_00000001" });
            outcome.Queries.Add(new QueryOutcome { Count = 0, Filter = "category = Windows", Subject = "Windows" });

            var env = FindResultBuilder.Build(plan, outcome);
            AssertValid(env);
            Assert.Equal(2, env.Count);
            var qs = env.Data!.Value.GetProperty("queries");
            Assert.Equal(2, qs.GetArrayLength());
            Assert.Equal(2, qs[0].GetProperty("count").GetInt32());
            Assert.Equal("h_00000001", qs[0].GetProperty("handle").GetString());
            Assert.Equal(0, qs[1].GetProperty("count").GetInt32());
            Assert.Equal("category = Doors | category = Windows", env.Searched!.Filter);
            Assert.Contains("2 Doors", env.Summary);
            Assert.Contains("0 Windows", env.Summary);
        }

        [Fact]
        public void FindResult_LinkedRowsKeepTheLinkInTheRef()
        {
            var plan = FindPlanOf("{\"queries\":[{\"category\":\"Structural Columns\",\"source\":\"link:STR_v3\"}]}");
            var outcome = new FindOutcome { Sources = { "link: STR_v3" }, Categories = { "Structural Columns" }, Handle = "h_00000002" };
            outcome.Rows.Add(new FoundRow { Ref = new ElementRef(77, 9001), Category = "Structural Columns", LinkName = "STR_v3" });
            outcome.Queries.Add(new QueryOutcome { Count = 1, Filter = "category = Structural Columns", Subject = "Structural Columns" });
            var env = FindResultBuilder.Build(plan, outcome);
            AssertValid(env);
            var r = env.Data!.Value.GetProperty("rows")[0];
            Assert.Equal(9001, r.GetProperty("ref").GetProperty("link").GetInt64());
            Assert.Equal("STR_v3", r.GetProperty("link").GetString());
            Assert.Contains("in link STR_v3", env.Summary);
        }

        [Fact]
        public void FindResult_HighlightIsReported()
        {
            var plan = FindPlanOf("{\"queries\":[{\"category\":\"Doors\"}],\"highlight\":true}");
            var outcome = new FindOutcome { Sources = { "host" }, Categories = { "Doors" }, Handle = "h_00000004",
                Highlight = new HighlightDto { Selected = 3, VisibleInView = 2 } };
            for (var i = 0; i < 3; i++) outcome.Rows.Add(new FoundRow { Ref = new ElementRef(i + 1, null), Category = "Doors" });
            outcome.Queries.Add(new QueryOutcome { Count = 3, Filter = "category = Doors", Subject = "Doors" });
            var env = FindResultBuilder.Build(plan, outcome);
            AssertValid(env);
            Assert.Equal(3, env.Highlight!.Selected);
            Assert.Equal(2, env.Highlight.VisibleInView);
            Assert.Contains("1 selected element is not visible in the active view", env.Next);
        }

        [Fact]
        public void NearMatches_RelaxOneConditionAtATime_InOrder()
        {
            var q = FindPlanOf("{\"queries\":[{\"text\":\"pintu kalis api\",\"level\":\"Level 3\",\"type\":\"FR\",\"room\":\"Lobi\",\"in_view\":true}]}").Queries[0];
            var steps = NearMatchPlanner.Relaxations(q, hostOnlyWithLinks: true);
            Assert.Equal(new[] { "level", "text", "type", "room", "in_view", "category", "source" }, steps.Select(s => s.Dropped));
            Assert.Null(steps[0].Query.Level);
            Assert.NotEmpty(steps[0].Query.Terms);              // only one condition dropped per step
            Assert.Empty(steps[1].Query.Terms);
            Assert.Equal("Level 3", steps[1].Query.Level);
            Assert.Equal(FindSourceKind.All, steps[6].Query.Source.Kind);
        }

        [Fact]
        public void NearMatches_InferredCategoryCanBeDroppedToo()
        {
            var q = FindPlanOf("{\"queries\":[{\"text\":\"tingkap\"}]}").Queries[0];
            var steps = NearMatchPlanner.Relaxations(q, hostOnlyWithLinks: false);
            var cat = Assert.Single(steps, s => s.Dropped == "category");
            Assert.Null(cat.Query.CategoryOst);
            Assert.Contains(cat.Query.Terms, t => t.Variants.Contains("window"));   // the word stays as text
        }

        [Fact]
        public void NearMatches_FormatAsTypeCountAndPlace()
        {
            Assert.Equal("Door - Fire Rated 60 min: 14 on Level 2",
                NearMatchPlanner.Format("Door - Fire Rated 60 min", 14, "Level 2", null));
            Assert.Equal("C1 300x300: 8 in link STR_v3", NearMatchPlanner.Format("C1 300x300", 8, null, "STR_v3"));
            Assert.Equal("Doors: 3", NearMatchPlanner.Format("Doors", 3, null, null));
        }
    }
}
