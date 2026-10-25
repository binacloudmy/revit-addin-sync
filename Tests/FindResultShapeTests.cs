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
        public void FindResult_OnlySourceUnloaded_SaysNothingCouldBeSearched()
        {
            var plan = FindPlanOf("{\"queries\":[{\"category\":\"Structural Columns\",\"source\":\"link:STR_v3\"}]}");
            var outcome = new FindOutcome { Categories = { "Structural Columns" }, NotSearched = { "link: STR_v3 (unloaded)" } };
            outcome.Queries.Add(new QueryOutcome { Count = 0, Filter = "category = Structural Columns", Subject = "Structural Columns" });
            var env = FindResultBuilder.Build(plan, outcome);
            AssertValid(env);
            Assert.Empty(env.Searched!.Sources);
            Assert.Equal("Nothing could be searched for Structural Columns. Not searched: link STR_v3 (unloaded).", env.Summary);
            Assert.Contains("STR_v3", env.Next);
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
        // ── inspect ───────────────────────────────────────────────────────

        private static InspectArgs InspectArgsOf(string json)
        {
            Assert.True(InspectArgs.TryParse(JsonDocument.Parse(json).RootElement.Clone(), out var a, out var error), error);
            return a;
        }

        private static InspectedElement Door(long id, int extraParams = 0)
        {
            var e = new InspectedElement { Ref = new ElementRef(id, null), Category = "Doors", Type = "Single Flush 900", Level = "Level 2" };
            e.Params.Add(new ParamRow { Name = "Mark", Value = "D" + id, Storage = "string" });
            e.Params.Add(new ParamRow { Name = "Sill Height", Value = "0 mm", Storage = "double", Unit = "mm" });
            e.Params.Add(new ParamRow { Name = "Comments", Value = null, Storage = "string" });
            e.Params.Add(new ParamRow { Name = "Area", Value = "1.89 m²", Storage = "double", Unit = "m²", ReadOnly = true });
            for (var i = 0; i < extraParams; i++) e.Params.Add(new ParamRow { Name = "P" + i.ToString("000"), Value = "v", Storage = "string" });
            return e;
        }

        [Fact]
        public void Inspect_ShapesParamsWithUnitsStorageAndReadOnly()
        {
            var a = InspectArgsOf("{\"target\":{\"ids\":[11]}}");
            var env = InspectResultBuilder.Build(a, new InspectPayload { Elements = { Door(11) }, TotalElements = 1 });
            AssertValid(env);
            Assert.Equal(1, env.Count);
            var el = env.Data!.Value.GetProperty("elements")[0];
            Assert.Equal(11, el.GetProperty("ref").GetProperty("id").GetInt64());
            var ps = el.GetProperty("params").EnumerateArray().ToList();
            var area = ps.Single(p => p.GetProperty("name").GetString() == "Area");
            Assert.Equal("1.89 m²", area.GetProperty("value").GetString());
            Assert.Equal("double", area.GetProperty("storage").GetString());
            Assert.True(area.GetProperty("read_only").GetBoolean());
            var mark = ps.Single(p => p.GetProperty("name").GetString() == "Mark");
            Assert.False(mark.TryGetProperty("read_only", out _));          // false is omitted
            Assert.Equal("Area", ps[0].GetProperty("name").GetString());    // filled values first, by name
            Assert.Equal("Comments", ps.Last().GetProperty("name").GetString());
            Assert.Contains("1 element", env.Summary);
        }

        [Fact]
        public void Inspect_FieldsKeepRequestedOrder_AndReportMissingWithSuggestions()
        {
            var a = InspectArgsOf("{\"target\":{\"ids\":[11]},\"fields\":[\"Sill Height\",\"Mark\",\"Fire Rateing\"]}");
            var d = Door(11);
            d.Params.RemoveAll(p => p.Name != "Sill Height" && p.Name != "Mark");
            d.Params.Reverse();
            d.Missing.Add(new MissingField { Field = "Fire Rateing", Suggestions = { "Fire Rating" } });
            var env = InspectResultBuilder.Build(a, new InspectPayload { Elements = { d }, TotalElements = 1 });
            AssertValid(env);
            var el = env.Data!.Value.GetProperty("elements")[0];
            Assert.Equal(new[] { "Sill Height", "Mark" }, el.GetProperty("params").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
            var missing = el.GetProperty("missing")[0];
            Assert.Equal("Fire Rateing", missing.GetProperty("field").GetString());
            Assert.Equal("Fire Rating", missing.GetProperty("suggestions")[0].GetString());
            Assert.Contains("Fire Rateing", env.Next);
        }

        [Fact]
        public void Inspect_CapsElementsAndParameters()
        {
            var a = InspectArgsOf("{\"target\":{\"handle\":\"h_7f3a91c2\"}}");
            var payload = new InspectPayload { TotalElements = 60, Handle = "h_7f3a91c2" };
            for (var i = 0; i < 60; i++) payload.Elements.Add(Door(i + 1, extraParams: 120));
            var env = InspectResultBuilder.Build(a, payload);
            AssertValid(env);
            var els = env.Data!.Value.GetProperty("elements");
            Assert.Equal(InspectResultBuilder.MaxElements, els.GetArrayLength());
            Assert.Equal(60, env.Count);
            Assert.Equal(InspectResultBuilder.MaxParams, els[0].GetProperty("params").GetArrayLength());
            Assert.True(els[0].GetProperty("params_omitted").GetInt32() > 0);
            Assert.False(els[InspectResultBuilder.MaxElementsWithAllParams].TryGetProperty("params", out _));
            Assert.Contains("fields", env.Next);
            Assert.Equal("h_7f3a91c2", env.Handle);
        }

        [Fact]
        public void Inspect_AllIdsMissing_IsNotFound_SomeMissingIsListed()
        {
            var a = InspectArgsOf("{\"target\":{\"ids\":[5,6]}}");
            var none = InspectResultBuilder.Build(a, new InspectPayload { MissingIds = { 5, 6 } });
            AssertValid(none);
            Assert.False(none.Ok);
            Assert.Equal(ErrorCodes.NotFound, none.Error!.Code);
            Assert.NotNull(none.Error.Candidates);

            var some = InspectResultBuilder.Build(a, new InspectPayload { Elements = { Door(5) }, TotalElements = 1, MissingIds = { 6 } });
            AssertValid(some);
            Assert.Equal(6, some.Data!.Value.GetProperty("missing_ids")[0].GetInt64());
            Assert.Contains("1 id was not found", some.Summary);
        }

        [Fact]
        public void Schedule_LabelsHeaderBlankGroupTotalAndDataRows()
        {
            var cols = new List<string> { "Mark", "Type", "Level" };
            var rows = new List<List<string>>
            {
                new() { "Mark", "Type", "Level" },
                new() { "", "", "" },
                new() { "Level 2", "", "" },
                new() { "D01", "Single Flush 900", "Level 2" },
                new() { "D02", "Single Flush 900", "Level 2" },
                new() { "Total: 2", "", "" },
                new() { "Grand total: 2", "", "" },
            };
            var labels = ScheduleRows.Label(cols, rows, grouped: true);
            Assert.Equal(new[] { "header", "blank", "group", "data", "data", "total", "total" }, labels.Select(l => l.Kind));
        }

        [Fact]
        public void Schedule_IsCutAt50RowsAndCountsDataRows()
        {
            var table = new ScheduleTable { Name = "Door Schedule", Id = 900, Columns = { "Mark", "Type" }, ElementCount = 120 };
            table.Rows.Add(new List<string> { "Mark", "Type" });
            for (var i = 1; i <= 120; i++) table.Rows.Add(new List<string> { "D" + i, "Single Flush" });
            var env = InspectResultBuilder.Build(InspectArgsOf("{\"schedule\":\"Door Schedule\"}"), new InspectPayload { Schedule = table });
            AssertValid(env);
            var s = env.Data!.Value.GetProperty("schedule");
            Assert.Equal(50, s.GetProperty("rows").GetArrayLength());
            Assert.Equal(121, s.GetProperty("total_rows").GetInt32());
            Assert.Equal(120, s.GetProperty("data_rows").GetInt32());
            Assert.Equal("header", s.GetProperty("rows")[0].GetProperty("kind").GetString());
            Assert.Contains("Door Schedule", env.Summary);
            Assert.Contains("120 data rows", env.Summary);
            Assert.Contains("50 of 121", env.Next);
        }
        // ── look ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData(2000, 1000, 1024, 1024, 512)]
        [InlineData(800, 3000, 1024, 273, 1024)]
        [InlineData(640, 480, 1024, 640, 480)]
        public void Look_FitLongEdge(int w, int h, int max, int ew, int eh)
        {
            var (fw, fh) = ImageLayout.FitLongEdge(w, h, max);
            Assert.Equal(ew, fw);
            Assert.Equal(eh, fh);
        }

        [Fact]
        public void Look_SideBySide_FitsWithin1024AndKeepsAspect()
        {
            var layout = ImageLayout.SideBySide(new[] { (1024, 768), (1024, 1024), (1024, 512) }, 1024, 8);
            Assert.True(layout.Width <= 1024 && layout.Height <= 1024);
            Assert.Equal(3, layout.Panels.Count);
            Assert.All(layout.Panels, p => Assert.Equal(layout.Height, p.Height));
            Assert.Equal(0, layout.Panels[0].X);
            Assert.Equal(layout.Panels[0].Width + 8, layout.Panels[1].X);
            Assert.InRange(layout.Panels[2].Width / (double)layout.Panels[2].Height, 1.9, 2.1);   // 1024x512 stays 2:1
            Assert.Equal(layout.Panels[2].X + layout.Panels[2].Width, layout.Width);
        }

        [Fact]
        public void Look_ReadsPngSizeFromTheHeader()
        {
            // 8-byte signature, IHDR length+type, width=300, height=200.
            var png = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52,
                0, 0, 0x01, 0x2C, 0, 0, 0, 0xC8, 8, 6, 0, 0, 0,
            };
            Assert.True(Png.TryReadSize(png, out var w, out var h));
            Assert.Equal(300, w);
            Assert.Equal(200, h);
            Assert.False(Png.TryReadSize(new byte[] { 1, 2, 3 }, out _, out _));
        }

        [Fact]
        public void Look_CaptionsGiveRealSizeInMetres()
        {
            Assert.Equal("3D view around 34 elements, about 42 m × 18 m (section box +10%).",
                LookText.ElementsCaption(34, 42.04, 18.2, multi: false));
            Assert.Equal("Plan, 3D and elevation around 1 element, about 0.9 m × 0.2 m.",
                LookText.ElementsCaption(1, 0.9, 0.2, multi: true));
            Assert.Equal("Active view 'Level 2 - Floor Plan' (1:100), about 42 m × 18 m.",
                LookText.ViewCaption("Active view", "Level 2 - Floor Plan", 100, 42, 18));
            Assert.Equal("View 'Sheet A1' (scale n/a).", LookText.ViewCaption("View", "Sheet A1", null, null, null));
        }

        [Fact]
        public void Look_EnvelopeCarriesTheImageAndTheOrientationOnlyRule()
        {
            var img = new ImageDto { Base64 = "iVBORw0KGgo=", Width = 1024, Height = 512, Caption = "3D view around 3 elements, about 4 m × 2 m (section box +10%)." };
            var env = LookText.Build(img, "h_7f3a91c2", new List<string>());
            AssertValid(env);
            Assert.True(env.Ok);
            Assert.Equal(img.Caption, env.Summary);
            Assert.Equal("image/png", env.Image!.MediaType);
            Assert.Equal("h_7f3a91c2", env.Handle);
            Assert.Contains("never to count or measure", env.Next);
        }
    }
}
