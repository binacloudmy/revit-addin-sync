// Plan clustering (Plans.cs: CadClassifier.ClusterPlans) and unit handling (Units.cs:
// Units.InferScale, Units.Resolve, Units.Normalize). Resolve takes an ACadSharp
// CadDocument for its header; a bare `new CadDocument()` (no file) is enough to set
// Header.InsUnits, so the lying-header rule is testable without a DWG on disk.

using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Types.Units;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimPlansUnitsTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        private static Wall W(double x1, double y1, double x2, double y2, double thickness = 200)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            double nx = -dy / len * thickness / 2, ny = dx / len * thickness / 2;
            return new Wall(S(x1 + nx, y1 + ny, x2 + nx, y2 + ny),
                            S(x1 - nx, y1 - ny, x2 - nx, y2 - ny));
        }

        private static List<Wall> Square(double size, double ox = 0, double oy = 0) => new()
        {
            W(ox, oy, ox + size, oy),
            W(ox + size, oy, ox + size, oy + size),
            W(ox + size, oy + size, ox, oy + size),
            W(ox, oy + size, ox, oy),
        };

        private static TextElement T(string text, double x, double y) => new()
        {
            P1 = new Point(x, y), P2 = new Point(x + 400, y + 100), Text = text,
        };

        private static CadDocument DocWithUnits(UnitsType units)
        {
            var doc = new CadDocument();
            doc.Header.InsUnits = units;
            return doc;
        }

        // ── ClusterPlans ─────────────────────────────────────────────────

        [Fact]
        public void Two_squares_8m_apart_are_two_clusters_ordered_by_area_descending()
        {
            // 4 m square at the origin; 6 m square starting at x = 12 000 (gap 8 000 > 5 000).
            var walls = Square(4000).Concat(Square(6000, ox: 12000)).ToList();

            var clusters = CadClassifier.ClusterPlans(walls);

            Assert.Equal(2, clusters.Count);
            Assert.Equal(36_000_000.0, clusters[0].Area, 3);
            Assert.Equal(16_000_000.0, clusters[1].Area, 3);
            Assert.Equal(4, clusters[0].Walls.Count);
            Assert.Equal(4, clusters[1].Walls.Count);
        }

        [Fact]
        public void Squares_4m_apart_stay_one_plan()
        {
            var walls = Square(4000).Concat(Square(6000, ox: 8000)).ToList();

            PlanCluster only = Assert.Single(CadClassifier.ClusterPlans(walls));
            Assert.Equal(8, only.Walls.Count);
        }

        [Fact]
        public void Labels_land_on_the_plan_that_contains_them()
        {
            var walls = Square(4000).Concat(Square(6000, ox: 12000)).ToList();
            var texts = new List<TextElement> { T("TINGKAT 1", 14000, 3000), T("TINGKAT BAWAH", 2000, 2000) };

            var clusters = CadClassifier.ClusterPlans(walls, texts);

            Assert.Equal("TINGKAT 1", Assert.Single(clusters[0].Texts).Text);
            Assert.Equal("TINGKAT BAWAH", Assert.Single(clusters[1].Texts).Text);
        }

        // ── Units.InferScale ─────────────────────────────────────────────

        [Fact]
        public void InferScale_reads_a_30000_unit_diagonal_as_millimetres()
        {
            Assert.Equal(1.0, Units.InferScale(new[] { S(0, 0, 18000, 24000) }));
        }

        [Fact]
        public void InferScale_reads_a_30_unit_diagonal_as_metres()
        {
            Assert.Equal(1000.0, Units.InferScale(new[] { S(0, 0, 18, 24) }));
        }

        [Fact]
        public void InferScale_reads_a_1181_unit_diagonal_as_inches()
        {
            Assert.Equal(25.4, Units.InferScale(new[] { S(0, 0, 708.6, 944.8) }));
        }

        [Fact]
        public void InferScale_reads_a_98_unit_diagonal_as_feet()
        {
            Assert.Equal(304.8, Units.InferScale(new[] { S(0, 0, 59.04, 78.72) }));
        }

        [Fact]
        public void InferScale_of_nothing_is_millimetres()
        {
            Assert.Equal(1.0, Units.InferScale(new List<Segment>()));
        }

        // ── Units.Resolve ────────────────────────────────────────────────

        [Fact]
        public void Resolve_believes_an_honest_header()
        {
            // Metres, 30-unit diagonal → 30 m: a building.
            var segments = new[] { S(0, 0, 18, 24) };

            Assert.Equal(1000.0, Units.Resolve(DocWithUnits(UnitsType.Meters), segments));
        }

        [Fact]
        public void Resolve_disbelieves_a_header_that_makes_the_plan_2km_wide()
        {
            // Millimetre drawing (90 m diagonal) declaring inches → 2.3 km, outside the
            // 3 m..1 km band, so the scale is inferred from the geometry instead.
            var segments = new[] { S(0, 0, 54000, 72000) };

            Assert.Equal(1.0, Units.Resolve(DocWithUnits(UnitsType.Inches), segments));
        }

        [Fact]
        public void Resolve_infers_when_the_header_is_unitless()
        {
            var segments = new[] { S(0, 0, 18, 24) };

            Assert.Equal(1000.0, Units.Resolve(DocWithUnits(UnitsType.Unitless), segments));
        }

        // ── Units.Normalize ──────────────────────────────────────────────

        [Fact]
        public void Normalize_scales_segments_arcs_and_text_into_millimetres()
        {
            var geometry = new List<GeometryElement>
            {
                S(0, 0, 10, 0),
                new Arc { Center = new Point(1, 1), Radius = 2, StartAngle = 0, EndAngle = 1 },
            };
            var text = new List<TextElement> { T("BILIK", 3, 4) };

            var (scaledGeometry, scaledText) = Units.Normalize(geometry, text, 25.4);

            var segment = Assert.IsType<Segment>(scaledGeometry[0]);
            Assert.Equal(254.0, segment.Length, 6);
            var arc = Assert.IsType<Arc>(scaledGeometry[1]);
            Assert.Equal(50.8, arc.Radius, 6);
            Assert.Equal(25.4, arc.Center.x, 6);
            Assert.Equal(1.0, arc.EndAngle, 6);   // angles are not lengths
            Assert.Equal(76.2, scaledText[0].P1.x, 6);
            Assert.Equal("BILIK", scaledText[0].Text);
        }
    }
}
