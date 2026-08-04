using BinaVibe.Mcp.Tools;
using Xunit;

namespace Tests;

public class CadSegmentStitcherTests
{
    private const double MmFt = 304.8;

    private static WallSeg Seg(double ax, double ay, double bx, double by, string layer = "WALL")
        => new(ax / MmFt, ay / MmFt, bx / MmFt, by / MmFt, layer);

    [Fact]
    public void MergesCollinearSegmentsAcrossSmallGap()
    {
        // Two collinear segments with a 900mm gap (door opening)
        var segs = new[]
        {
            Seg(0, 0, 2000, 0),      // left segment
            Seg(2900, 0, 5000, 0),   // right segment (900mm gap)
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm(maxGapMm: 1500));

        Assert.Single(result.Segments);
        Assert.Equal(1, result.MergedCount);
        // Should span full 5000mm
        var s = result.Segments[0];
        Assert.True(s.Len * MmFt > 4900); // close to 5000mm
    }

    [Fact]
    public void DoesNotMergeAcrossLargeGap()
    {
        // Two collinear segments with 2000mm gap (too large)
        var segs = new[]
        {
            Seg(0, 0, 2000, 0),
            Seg(4000, 0, 6000, 0),   // 2000mm gap
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm(maxGapMm: 1500));

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(0, result.MergedCount);
    }

    [Fact]
    public void DoesNotMergeNonCollinear()
    {
        // Parallel but not collinear (200mm apart perpendicular)
        var segs = new[]
        {
            Seg(0, 0, 2000, 0),
            Seg(2500, 200, 5000, 200),  // 200mm offset perpendicular
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm(collinearTolMm: 50));

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(0, result.MergedCount);
    }

    [Fact]
    public void MergesMultipleSegmentsInChain()
    {
        // Three segments with two gaps
        var segs = new[]
        {
            Seg(0, 0, 1500, 0),
            Seg(2400, 0, 4000, 0),     // 900mm gap
            Seg(4800, 0, 6000, 0),     // 800mm gap
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm(maxGapMm: 1000));

        Assert.Single(result.Segments);
        Assert.Equal(2, result.MergedCount);
    }

    [Fact]
    public void FiltersSmallRectangle()
    {
        // 400x400mm closed rectangle (column pad)
        var segs = new[]
        {
            Seg(0, 0, 400, 0),        // bottom
            Seg(400, 0, 400, 400),    // right
            Seg(400, 400, 0, 400),    // top
            Seg(0, 400, 0, 0),        // left
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm(maxColumnSizeMm: 800));

        Assert.Empty(result.Segments);
        Assert.Equal(1, result.FilteredRectangles);
    }

    [Fact]
    public void KeepsLargeRectangle()
    {
        // 1000x1000mm rectangle (too big to be column)
        var segs = new[]
        {
            Seg(0, 0, 1000, 0),
            Seg(1000, 0, 1000, 1000),
            Seg(1000, 1000, 0, 1000),
            Seg(0, 1000, 0, 0),
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm(maxColumnSizeMm: 800));

        Assert.Equal(4, result.Segments.Count);
        Assert.Equal(0, result.FilteredRectangles);
    }

    [Fact]
    public void RespectsLayerBoundaries()
    {
        // Collinear but different layers — should NOT merge
        var segs = new[]
        {
            Seg(0, 0, 2000, 0, "WALL"),
            Seg(2500, 0, 5000, 0, "PARTITION"),
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm(maxGapMm: 1500));

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(0, result.MergedCount);
    }

    [Fact]
    public void HandlesOverlappingSegments()
    {
        // Two overlapping segments
        var segs = new[]
        {
            Seg(0, 0, 3000, 0),
            Seg(2000, 0, 5000, 0),  // overlaps by 1000mm
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm());

        Assert.Single(result.Segments);
        // Should span 0-5000
        var s = result.Segments[0];
        Assert.True(s.Len * MmFt > 4900);
    }

    [Fact]
    public void HandlesVerticalSegments()
    {
        // Vertical wall with door gap
        var segs = new[]
        {
            Seg(0, 0, 0, 2000),
            Seg(0, 2900, 0, 5000),
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm(maxGapMm: 1500));

        Assert.Single(result.Segments);
        Assert.Equal(1, result.MergedCount);
    }

    [Fact]
    public void HandlesDiagonalSegments()
    {
        // 45-degree wall with gap
        var segs = new[]
        {
            Seg(0, 0, 1000, 1000),
            Seg(1500, 1500, 2500, 2500),  // ~707mm gap
        };

        var result = CadSegmentStitcher.Stitch(segs, StitchOptions.FromMm(maxGapMm: 1000));

        Assert.Single(result.Segments);
    }
}
