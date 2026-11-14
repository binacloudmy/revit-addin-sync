#nullable enable
// The "Gabung" result view, G3: dots that would overlap on screen become one badge with their count. Two marks are
// together when their centres are closer than 1.2 dot diameters (10 DIP dots: 12 DIP); the grouping is a grid, so a
// few thousand marks stay fast.
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Highlights;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class MarkClusterTests
    {
        private static List<List<int>> G(params (double, double)[] pts) => MarkCluster.Group(pts, MarkKey.DotDip);

        [Fact]
        public void DotsCloserThanOnePointTwoDiameters_GoTogether_FartherOnesStayAlone()
        {
            Assert.Equal(12.0, MarkCluster.Together * MarkKey.DotDip, 6);
            Assert.Single(G((0, 0), (11.9, 0)));                     // just inside
            Assert.Equal(2, G((0, 0), (12.1, 0)).Count);             // just outside
            Assert.Single(G((100, 100), (108, 108)));                // 11.3 on the diagonal
            Assert.Equal(2, G((100, 100), (109, 109)).Count);        // 12.7
        }

        [Fact]
        public void AChainOfCloseDots_IsOneBadge_AndTheGroupsKeepTheirOrder()
        {
            var groups = G((0, 0), (500, 500), (10, 0), (20, 0), (30, 0), (505, 500));
            Assert.Equal(2, groups.Count);
            Assert.Equal(new[] { 0, 2, 3, 4 }, groups[0]);           // 0 and 30 are far apart, joined through 10 and 20
            Assert.Equal(new[] { 1, 5 }, groups[1]);
        }

        [Fact]
        public void APointThatIsNotANumber_StaysAlone_AndNothingIsNothing()
        {
            var groups = G((0, 0), (double.NaN, double.NaN), (1, 1));
            Assert.Equal(2, groups.Count);
            Assert.Equal(new[] { 1 }, groups[1]);
            Assert.Empty(MarkCluster.Group(new (double, double)[0], 10));
            Assert.Single(G((5, 5)));
            Assert.Equal(2, MarkCluster.Group(new[] { (0.0, 0.0), (1.0, 0.0) }, 0).Count);   // no size: nothing goes together
        }

        [Fact]
        public void NegativeCoordinates_AcrossACellEdge_StillGoTogether()
        {
            Assert.Single(G((-0.5, -0.5), (0.5, 0.5)));
            Assert.Single(G((-11.5, 0), (0, 0)));
        }

        [Fact]
        public void FiveThousandMarks_AreGroupedQuickly_AndEveryMarkIsInOneGroup()
        {
            // A ceiling grid of 50 x 100 fixtures, 20 DIP apart (each alone), plus a dense block of 400 on top of each other.
            var pts = new List<(double, double)>();
            for (var x = 0; x < 100; x++) for (var y = 0; y < 50; y++) pts.Add((x * 20.0, y * 20.0));
            for (var i = 0; i < 400; i++) pts.Add((3000 + (i % 20) * 0.5, 3000 + (i / 20) * 0.5));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var groups = MarkCluster.Group(pts, MarkKey.DotDip);
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 2000, watch.ElapsedMilliseconds + " ms");
            Assert.Equal(5001, groups.Count);
            Assert.Equal(pts.Count, groups.Sum(g => g.Count));
            Assert.Equal(pts.Count, groups.SelectMany(g => g).Distinct().Count());
            Assert.Equal(400, groups.Last().Count);
        }

        [Theory]
        [InlineData(2, "2")]
        [InlineData(4, "4")]
        [InlineData(99, "99")]
        [InlineData(100, "99+")]
        [InlineData(3450, "99+")]
        public void ABadgeCounts_Above99Reads99Plus(int count, string text) => Assert.Equal(text, MarkCluster.CountLabel(count));

        [Fact]
        public void ANumberedBadge_ShowsItsLowestNumber_WithPlusWhenItHoldsMore()
        {
            Assert.Equal("1", MarkCluster.NumberLabel(1, more: false));
            Assert.Equal("3+", MarkCluster.NumberLabel(3, more: true));
        }

        [Fact]
        public void TheDotIsOneSize_TheSizeStepsAreForFrames()
        {
            Assert.Equal(10, MarkKey.DotDip);
            var src = ResultMarksFocusTests.Src("UI", "Copilot", "Highlights", "InModelResult.cs");
            Assert.Contains("foreach (var m in _marks.Where(m => m.Frame))", src);                       // SizeMarks sizes frames only
            Assert.Contains("MarkCluster.Group(dots.Select(m => screen(m.Point)).ToList(), MarkKey.DotDip)", src);
            Assert.Contains("MarkCluster.CountLabel(d.Marks.Count)", src);
            Assert.Contains("if (!own && !Hidden && _controls.Count > 0 && Relaid(uidoc, view)) Redraw(app);", src);
        }
    }
}
