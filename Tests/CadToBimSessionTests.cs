// CadToBimSession — the pane's correction state between detect and Confirm.
// Pinned: erase is a toggle that hides a wall from Active and Pending; brushed
// (Forced) walls join Active and can be erased too; after a build, Pending
// excludes the built walls while Active still shows them; erasing a built wall
// never touches BuiltWallIds (undo is the drafter's tool); the median thickness
// is recomputed when the detect pass hands over a new wall list.
//
// [Collection("Cad2Bim")]: Cad2Bim.Wall's thresholds (SMin/SMax/MinFaceAspect/
// MinFaceLength) are STATIC and mutable. xunit runs test classes in parallel, so
// every class that constructs a Wall or touches those statics shares this
// collection and runs serially.

using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace RevitWebAppSync.Tests
{
    [Collection("Cad2Bim")]
    public class CadToBimSessionTests
    {
        /// <summary>A horizontal wall: faces at y and y + thickness, 0..length along x.</summary>
        private static Wall WallAt(double y, double thickness = 100, double length = 1000) =>
            new(new Segment(new Point(0, y), new Point(length, y)),
                new Segment(new Point(0, y + thickness), new Point(length, y + thickness)));

        private static CadToBimSession Session(params Wall[] walls)
        {
            var session = new CadToBimSession();
            session.SetWalls(walls.ToList());
            return session;
        }

        [Fact]
        public void Erase_RemovesFromActiveAndPending_TwiceRestores()
        {
            Wall a = WallAt(0), b = WallAt(1000);
            var s = Session(a, b);

            s.ToggleErase(a);
            Assert.DoesNotContain(a, s.Active());
            Assert.DoesNotContain(a, s.Pending());
            Assert.Contains(b, s.Active());
            Assert.Equal(1, s.ErasedCount);

            s.ToggleErase(a);
            Assert.Contains(a, s.Active());
            Assert.Contains(a, s.Pending());
            Assert.Equal(0, s.ErasedCount);
        }

        [Fact]
        public void Forced_AppearsInActive_DedupedByReference()
        {
            Wall a = WallAt(0), brushed = WallAt(2000);
            var s = Session(a);

            s.AddForced(new[] { brushed });
            s.AddForced(new[] { brushed });     // same instance again
            s.AddForced(new[] { a });           // already a detected wall

            Assert.Equal(new[] { a, brushed }, s.Active());
            Assert.Equal(1, s.ForcedCount);
            Assert.Single(s.Forced);
        }

        [Fact]
        public void Erase_AppliesToForcedWallsToo()
        {
            Wall brushed = WallAt(2000);
            var s = Session();
            s.AddForced(new[] { brushed });

            s.ToggleErase(brushed);
            Assert.Empty(s.Active());
            Assert.Empty(s.Pending());

            s.ToggleErase(brushed);
            Assert.Equal(new[] { brushed }, s.Active());
        }

        [Fact]
        public void MarkBuilt_PendingExcludesBuilt_ActiveStillIncludes()
        {
            Wall a = WallAt(0), b = WallAt(1000), later = WallAt(2000);
            var s = Session(a, b);

            var report = new BuildReport { Walls = 2 };
            report.WallIds[a] = 1001;
            report.WallIds[b] = 1002;
            s.MarkBuilt(report);

            Assert.Equal(2, s.BuiltCount);
            Assert.Equal(new[] { a, b }, s.Active());
            Assert.Empty(s.Pending());

            s.AddForced(new[] { later });
            Assert.Equal(new[] { later }, s.Pending());      // only the new wall goes to the next Confirm
            Assert.Equal(3, s.Active().Count);
        }

        [Fact]
        public void EraseAfterBuild_KeepsBuiltWallIds()
        {
            Wall a = WallAt(0);
            var s = Session(a);
            var report = new BuildReport();
            report.WallIds[a] = 77;
            s.MarkBuilt(report);

            s.ToggleErase(a);
            Assert.DoesNotContain(a, s.Active());
            Assert.True(s.BuiltWallIds.ContainsKey(a));
            Assert.Equal(77, s.BuiltWallIds[a]);
        }

        [Fact]
        public void MarkBuilt_NullOrFailedReport_ChangesNothing()
        {
            Wall a = WallAt(0);
            var s = Session(a);
            s.MarkBuilt(null);
            s.MarkBuilt(new BuildReport { Error = "rolled back" });
            Assert.Empty(s.BuiltWallIds);
            Assert.Equal(new[] { a }, s.Pending());
        }

        [Fact]
        public void SetWalls_RecomputesMedianThickness()
        {
            var s = new CadToBimSession();
            Assert.Equal(100, s.MedianThicknessMm);                // fallback before any detect

            s.SetWalls(new List<Wall> { WallAt(0, 230), WallAt(1000, 100), WallAt(2000, 100) });
            Assert.Equal(100, s.MedianThicknessMm, 6);

            s.SetWalls(new List<Wall> { WallAt(0, 230), WallAt(1000, 230), WallAt(2000, 100) });
            Assert.Equal(230, s.MedianThicknessMm, 6);

            s.SetWalls(new List<Wall>());
            Assert.Equal(230, s.MedianThicknessMm, 6);             // empty detect keeps the last value
        }

        [Fact]
        public void Median_IsUpperMiddleForEvenCounts_SameAsTheOldCommand()
        {
            var walls = new List<Wall> { WallAt(0, 100), WallAt(1000, 230) };
            Assert.Equal(230, CadToBimSession.Median(walls, 50), 6);
            Assert.Equal(50, CadToBimSession.Median(new List<Wall>(), 50), 6);
        }

        [Fact]
        public void SetWalls_DropsStaleErasures_KeepsForcedAndBuilt()
        {
            Wall old = WallAt(0), brushed = WallAt(3000);
            var s = Session(old);
            s.AddForced(new[] { brushed });
            s.ToggleErase(old);
            s.ToggleErase(brushed);
            var report = new BuildReport();
            report.WallIds[old] = 5;
            s.MarkBuilt(report);

            Wall fresh = WallAt(0);                               // re-detect: new instances
            s.SetWalls(new List<Wall> { fresh });

            Assert.Equal(1, s.ErasedCount);                       // `old` pruned, `brushed` kept
            Assert.Contains(brushed, s.Erased);
            Assert.Single(s.Forced);
            Assert.True(s.BuiltWallIds.ContainsKey(old));         // history is never rewritten here
            Assert.Equal(new[] { fresh }, s.Active());
        }

        [Fact]
        public void ToggleErase_Null_IsIgnored()
        {
            var s = Session(WallAt(0));
            s.ToggleErase(null);
            Assert.Equal(0, s.ErasedCount);
        }

        [Fact]
        public void CenterlineKey_IsDirectionIndependent_AndRoundsToMm()
        {
            Wall leftToRight = new(new Segment(new Point(0, 0), new Point(1000, 0)),
                                   new Segment(new Point(0, 100), new Point(1000, 100)));
            Wall rightToLeft = new(new Segment(new Point(1000.2, 0), new Point(0.3, 0)),
                                   new Segment(new Point(1000.2, 100), new Point(0.3, 100)));
            Assert.Equal("0,0,1000,0", CadToBimSession.CenterlineKey(leftToRight));
            Assert.Equal(CadToBimSession.CenterlineKey(leftToRight), CadToBimSession.CenterlineKey(rightToLeft));
            Assert.NotEqual(CadToBimSession.CenterlineKey(leftToRight), CadToBimSession.CenterlineKey(WallAt(2000)));
        }

        [Fact]
        public void CarryForwardFrom_ReKeysErasedAndBuilt_ByCenterline_AndKeepsForcedByIdentity()
        {
            // First detect: walls a, b; drafter erases a, builds b, brushes in c (then erases it).
            Wall a = WallAt(0), b = WallAt(1000), brushed = WallAt(3000);
            var first = Session(a, b);
            first.ToggleErase(a);
            var report = new BuildReport();
            report.WallIds[b] = 42;
            first.MarkBuilt(report);
            first.AddForced(new[] { brushed });
            first.ToggleErase(brushed);

            // Re-detect: the same two centrelines come back as NEW instances, plus a third wall.
            Wall a2 = WallAt(0), b2 = WallAt(1000), d2 = WallAt(5000);
            var second = new CadToBimSession { DrawingPath = first.DrawingPath };
            second.SetWalls(new List<Wall> { a2, b2, d2 });

            second.CarryForwardFrom(first);

            Assert.Contains(a2, second.Erased);                    // erase mark followed the centreline
            Assert.DoesNotContain(a, second.Erased);               // the stale instance is not dragged along
            Assert.Equal(42, second.BuiltWallIds[b2]);             // built id followed the centreline
            Assert.False(second.BuiltWallIds.ContainsKey(b));
            Assert.Equal(new[] { brushed }, second.Forced);        // brushed walls survive by identity
            Assert.Contains(brushed, second.Erased);               // and so does their erased state
            Assert.Equal(new[] { d2 }, second.Pending());          // only the genuinely new wall goes to the next Confirm
            Assert.Equal(2, second.ErasedCount);

            second.CarryForwardFrom(null);                         // no-op, never throws
            Assert.Equal(2, second.ErasedCount);
        }
    }
}
