// Cad2BimBrush — "drag a box over what the automatic pass missed". Inside the box
// the guards come off (no aspect test, no minimum face length), a lone line ≥ 500 mm
// becomes a wall at the session's median thickness, and a box full of hatch strokes
// becomes one wall from its extent. Tested on hand-built CadModels through
// RunOnModel; Run(path, …) only adds the DWG read in front of it.
//
// [Collection("Cad2Bim")]: Wall.MinFaceAspect / MinFaceLength / SMin / SMax are static
// mutable — see CadToBimSessionTests. The restore test below asserts the brush puts
// back the PREVIOUS values, not the defaults.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace RevitWebAppSync.Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimBrushTests
    {
        private static readonly BoxMm Box = new(-100, -100, 2100, 300);

        private static Segment Seg(double x1, double y1, double x2, double y2) =>
            new(new Point(x1, y1), new Point(x2, y2));

        private static CadModel Model(params Segment[] segments)
        {
            var model = new CadModel();
            model.Segments.AddRange(segments);
            return model;
        }

        [Fact]
        public void TwoShortFaces_PairWithGuardsOff()
        {
            // 100 mm pieces 115 apart: the automatic pass rejects them twice over
            // (MinFaceLength 200, aspect 100 < 115 × 1.5). Inside a box they are a wall.
            var r = Cad2BimBrush.RunOnModel(Model(Seg(0, 0, 100, 0), Seg(0, 115, 100, 115)), Box, 100);

            Wall wall = Assert.Single(r.Walls);
            Assert.Equal(115, wall.Thickness, 3);
            Assert.Equal(100, wall.Centerline.Length, 3);
            Assert.Equal(1, r.Paired);
            Assert.Equal(0, r.SingleLine);
            Assert.Equal(0, r.Cloud);
            Assert.Equal("1 walls paired, 0 single-line, 0 cloud", r.Note);
        }

        [Fact]
        public void LoneLongFace_BecomesSingleLineWall_AtMedianThickness()
        {
            var r = Cad2BimBrush.RunOnModel(Model(Seg(0, 0, 800, 0)), Box, medianThicknessMm: 100);

            Wall wall = Assert.Single(r.Walls);
            Assert.Equal(100, wall.Thickness, 6);
            Assert.Equal(800, wall.Centerline.Length, 3);
            Assert.Equal(0, wall.Centerline.P1.y, 6);            // centreline IS the drawn line
            Assert.Equal(1, r.SingleLine);
            Assert.Equal("0 walls paired, 1 single-line, 0 cloud", r.Note);
        }

        [Fact]
        public void LoneShortFace_IsNeitherWallNorCloud()
        {
            // 300 mm < 500 mm single-line floor; two endpoints are not a cloud (< 4 points).
            var r = Cad2BimBrush.RunOnModel(Model(Seg(0, 0, 300, 0)), Box, 100);

            Assert.Empty(r.Walls);
            Assert.Contains("no wall shape", r.Note);
            Assert.Contains("1 lines", r.Note);
        }

        [Fact]
        public void HatchCloud_FallsBackToOneWallFromExtent()
        {
            // 40 strokes, 20 mm long, every one at a different angle (4° apart, so none
            // pairs even with the guards off), filling a 2000 × 150 band.
            var strokes = new List<Segment>();
            for (int i = 0; i < 40; i++)
            {
                double angle = (5 + 4 * i) * Math.PI / 180.0;
                double cx = 25 + 50 * i;
                double cy = i % 2 == 0 ? 10 : 140;
                double dx = 10 * Math.Cos(angle), dy = 10 * Math.Sin(angle);
                strokes.Add(Seg(cx - dx, cy - dy, cx + dx, cy + dy));
            }

            var r = Cad2BimBrush.RunOnModel(Model(strokes.ToArray()), Box, 100);

            Wall wall = Assert.Single(r.Walls);
            Assert.InRange(wall.Thickness, 140, 160);
            Assert.InRange(wall.Centerline.Length, 1800, 2100);
            Assert.Equal(0, r.Paired);
            Assert.Equal(1, r.Cloud);
            Assert.Equal("0 walls paired, 0 single-line, 1 cloud", r.Note);
        }

        [Fact]
        public void EmptyBox_ReportsNoLinework()
        {
            var r = Cad2BimBrush.RunOnModel(Model(Seg(5000, 5000, 6000, 5000)), Box, 100);

            Assert.Empty(r.Walls);
            Assert.Equal("no linework in box", r.Note);
        }

        [Fact]
        public void OnlyLineworkWithMidpointInsideBox_IsRead()
        {
            // Two faces inside, one 800 mm loner outside: exactly one wall.
            var r = Cad2BimBrush.RunOnModel(
                Model(Seg(0, 0, 100, 0), Seg(0, 115, 100, 115), Seg(3000, 0, 3800, 0)), Box, 100);

            Assert.Single(r.Walls);
            Assert.Equal(0, r.SingleLine);
        }

        [Fact]
        public void PochéOutlineInsideBox_IsAWall()
        {
            // A closed 1200 × 150 outline (hatch boundary) with no stroke linework.
            var model = new CadModel();
            model.Outlines.Add(new List<Point>
            {
                new(0, 0), new(1200, 0), new(1200, 150), new(0, 150),
            });

            var r = Cad2BimBrush.RunOnModel(model, Box, 100);

            Wall wall = Assert.Single(r.Walls);
            Assert.Equal(150, wall.Thickness, 3);
            Assert.Equal(1, r.Paired);
        }

        [Fact]
        public void RestoresStaticThresholds_ToTheirPreviousValues()
        {
            double aspectBefore = Wall.MinFaceAspect;
            double minFaceBefore = Wall.MinFaceLength;
            try
            {
                Wall.MinFaceAspect = 7.25;          // deliberately NOT the defaults
                Wall.MinFaceLength = 333;

                Cad2BimBrush.RunOnModel(Model(Seg(0, 0, 100, 0), Seg(0, 115, 100, 115)), Box, 100);

                Assert.Equal(7.25, Wall.MinFaceAspect);
                Assert.Equal(333, Wall.MinFaceLength);
            }
            finally
            {
                Wall.MinFaceAspect = aspectBefore;
                Wall.MinFaceLength = minFaceBefore;
            }
        }

        [Fact]
        public void SingleLineWall_OutsideThicknessRange_IsNull()
        {
            Assert.Null(Cad2BimBrush.SingleLineWall(Seg(0, 0, 800, 0), Wall.SMax + 100));
            Assert.Null(Cad2BimBrush.SingleLineWall(Seg(0, 0, 0, 0), 100));       // zero length
        }

        [Fact]
        public void BoxMm_Normalised_AcceptsAnyTwoCorners_AndContainsIsInclusive()
        {
            var box = BoxMm.Normalised(10, 20, -5, -8);
            Assert.Equal(new BoxMm(-5, -8, 10, 20), box);
            Assert.Equal(15, box.Width);
            Assert.Equal(28, box.Height);
            Assert.True(box.Contains(10, 20));
            Assert.True(box.Contains(new Point(-5, -8)));
            Assert.False(box.Contains(10.001, 0));
        }
    }
}
