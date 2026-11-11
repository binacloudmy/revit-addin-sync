#nullable enable
// Home "<Category> in view" card: dominant category, top 4 types + "N more types",
// proportions and the value equality Home relies on to avoid redraw loops.

using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ViewSummaryTests
    {
        private static IEnumerable<(string, string)> Many(string cat, string type, int n) => Enumerable.Repeat((cat, type), n);

        [Fact]
        public void NoElements_IsNull()
        {
            Assert.Null(ViewSummary.Build(null));
            Assert.Null(ViewSummary.Build(new List<(string, string)>()));
            Assert.Null(ViewSummary.Build(new[] { ("", "x"), ("  ", "y") }));
        }

        [Fact]
        public void Board1Numbers_TopFourPlusMoreTypes()
        {
            var els = Many("Lighting Fixtures", "Fluorescent 2×14W", 126)
                .Concat(Many("Lighting Fixtures", "Downlight 1×13W", 38))
                .Concat(Many("Lighting Fixtures", "Wall light 1×28W", 22))
                .Concat(Many("Lighting Fixtures", "Downlight 1×8W", 14));
            // six more types, 35 elements
            var more = new[] { 10, 9, 6, 5, 3, 2 };
            for (var i = 0; i < more.Length; i++) els = els.Concat(Many("Lighting Fixtures", "Other " + i, more[i]));

            var s = ViewSummary.Build(els)!;

            Assert.Equal("Lighting Fixtures in view", s.Title);
            Assert.Equal("235 · 10 types", s.Right);
            Assert.Equal(5, s.Segments.Count);
            Assert.Equal(new[] { "Fluorescent 2×14W", "Downlight 1×13W", "Wall light 1×28W", "Downlight 1×8W", "6 more types" }, s.Segments.Select(x => x.Label));
            Assert.Equal(new[] { 126, 38, 22, 14, 35 }, s.Segments.Select(x => x.Count));
            Assert.True(s.Segments[4].IsMore);
            Assert.False(s.Segments[0].IsMore);
            Assert.Equal(235, s.Segments.Sum(x => x.Count));
            Assert.Equal(1.0, s.Segments.Sum(x => x.Fraction), 6);
            Assert.Equal(126.0 / 235, s.Segments[0].Fraction, 6);
            Assert.Equal("235 Lighting Fixtures by type: Fluorescent 2×14W 126, Downlight 1×13W 38, Wall light 1×28W 22, Downlight 1×8W 14, 6 more types 35", s.Description);
        }

        [Fact]
        public void FewTypes_HaveNoMoreBucket_AndSingularWording()
        {
            var one = ViewSummary.Build(Many("Doors", "Single 0915", 3))!;
            Assert.Equal("Doors in view", one.Title);
            Assert.Equal("3 · 1 type", one.Right);
            Assert.Single(one.Segments);
            Assert.Equal(1.0, one.Segments[0].Fraction, 6);

            var four = ViewSummary.Build(Many("Doors", "A", 4).Concat(Many("Doors", "B", 3)).Concat(Many("Doors", "C", 2)).Concat(Many("Doors", "D", 1)))!;
            Assert.Equal(4, four.Segments.Count);
            Assert.DoesNotContain(four.Segments, x => x.IsMore);
        }

        [Fact]
        public void FiveTypes_FifthIsOneMoreType()
        {
            var els = Many("Doors", "A", 5).Concat(Many("Doors", "B", 4)).Concat(Many("Doors", "C", 3)).Concat(Many("Doors", "D", 2)).Concat(Many("Doors", "E", 1));
            var s = ViewSummary.Build(els)!;
            Assert.Equal("1 more type", s.Segments.Last().Label);
            Assert.Equal(1, s.Segments.Last().Count);
            Assert.Equal("15 · 5 types", s.Right);
        }

        [Fact]
        public void DominantCategory_IsTheLargest_TiesByName_AndOnlyItsTypesCount()
        {
            var els = Many("Doors", "D1", 5).Concat(Many("Walls", "W1", 9)).Concat(Many("Walls", "W2", 1)).Concat(Many("Windows", "X", 10));
            var s = ViewSummary.Build(els)!;
            Assert.Equal("Walls", s.Category);            // 10 vs 10: Walls before Windows
            Assert.Equal(10, s.Total);
            Assert.Equal(2, s.TypeCount);
        }

        [Fact]
        public void BlankType_IsNoType_AndEqualCountsSortByName()
        {
            var s = ViewSummary.Build(new[] { ("Rooms", ""), ("Rooms", "  "), ("Rooms", "B"), ("Rooms", "A") })!;
            Assert.Equal(new[] { "No type", "A", "B" }, s.Segments.Select(x => x.Label));
            Assert.Equal(2, s.Segments[0].Count);
        }

        [Fact]
        public void ValueEquality_SoHomeDoesNotRedrawForAnUnchangedView()
        {
            var a = ViewSummary.Build(Many("Doors", "A", 3).Concat(Many("Doors", "B", 2)))!;
            var b = ViewSummary.Build(Many("Doors", "A", 3).Concat(Many("Doors", "B", 2)))!;
            var c = ViewSummary.Build(Many("Doors", "A", 3).Concat(Many("Doors", "B", 1)))!;
            Assert.True(a.Equals(b));
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.False(a.Equals(c));
            Assert.True(ViewSummary.Same(null, null));
            Assert.False(ViewSummary.Same(a, null));
            // the tuple Home keeps as its scope compares through it
            Assert.True((("v", 1, 0, (ViewSummary?)a)).Equals(("v", 1, 0, (ViewSummary?)b)));
            Assert.False((("v", 1, 0, (ViewSummary?)a)).Equals(("v", 1, 0, (ViewSummary?)null)));
        }

        // Home's wiring lives in Revit/WPF code that the harness cannot load: source-text checks.
        [Fact]
        public void Home_ReadsTheSummaryOnRevitsThread_AndHidesTheCardWhenNull()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var home = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "UI/Copilot/Screens/ChatView.Home.cs"));
            Assert.Contains("ReadViewSummary(uidoc.Document, view)", home);                 // inside ReadScope
            Assert.Contains("RevitThread.RunAsync(app => ReadScope(app)", home);
            Assert.Contains("if (summary != null) col.Children.Add(HomeParts.ViewSummaryCard(summary))", home);
            // the Revit API is read in ReadViewSummary only, never in HomeState
            var homeState = home.Substring(home.IndexOf("private FrameworkElement HomeState()", System.StringComparison.Ordinal));
            Assert.DoesNotContain("FilteredElementCollector", homeState);
        }
    }
}
