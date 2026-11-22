using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-469, reworked by AI-504: just-under / on / just-over for every numeric threshold of the
    /// spec ("Peraturan comply"), pinned where the code and the spec row meet. The minimum is the
    /// documented one, compared exactly: JKR gives no rounding rule (Q1). A value that is under the
    /// minimum but prints as it (2999.6 shows as "3000 mm"; A1.3 prints one decimal) is listed for a
    /// person and never fails. A4.1 and Dc.1 have no tolerance at all (Q2, Q11).
    /// Table of spec row → code → verdict: Tests/data/spec_drift_audit.md.
    /// </summary>
    public class SpecThresholdBoundaryTests
    {
        private static List<SpecResult> Run(SpecModel m, string id) =>
            SpecChecks.Rules[id](m.Normalize()).ToList();

        private static bool Failed(SpecModel m, string id) => Run(m, id).Any(r => r.Status == "fail");

        private static SpecType T(string cat, string family, string type, double? width = null, double? height = null, double? thickness = null)
        {
            var t = new SpecType { Category = cat, FamilyName = family, TypeName = type, InstanceCount = 1, TypeId = 5000 };
            t.InstanceIds.Add(100);
            if (width != null) t.Numbers["Width"] = width.Value;
            if (height != null) t.Numbers["Height"] = height.Value;
            if (thickness != null) t.Numbers["Thickness"] = thickness.Value;
            return t;
        }

        // ── A1.1 ceiling height: ≥ 3000 mm (basement ≥ 2500 mm) ──

        private static SpecModel Ceiling(double heightMm, double levelElevation = 0)
        {
            var m = new SpecModel();
            m.Levels.Add(new SpecLevel { Name = "01", Elevation = levelElevation });
            m.Ceilings.Add(new SpecCeiling { Id = 1, LevelName = "01", HeightOffset = heightMm });
            return m;
        }

        [Theory]
        [InlineData(2999.0, true)]
        [InlineData(2999.4, true)]
        [InlineData(2999.5, false)]   // rounds to 3000 mm: the note could not say "3000 < 3000"
        [InlineData(3000.0, false)]
        [InlineData(3000.6, false)]
        public void A1_1_storey_minimum_is_3000_mm(double mm, bool fails) =>
            Assert.Equal(fails, Failed(Ceiling(mm), "A1.1"));

        [Theory]
        [InlineData(2499.0, true)]
        [InlineData(2499.4, true)]
        [InlineData(2500.0, false)]
        [InlineData(2500.6, false)]
        public void A1_1_basement_minimum_is_2500_mm(double mm, bool fails) =>
            Assert.Equal(fails, Failed(Ceiling(mm, levelElevation: -3500), "A1.1"));

        // ── A1.2 door size: single ≥ 900, double ≥ 1500, clear height ≥ 2100 ──

        [Theory]
        [InlineData("Single", 899.0, 2100, true)]
        [InlineData("Single", 899.4, 2100, true)]
        [InlineData("Single", 899.5, 2100, false)]
        [InlineData("Single", 900.0, 2100, false)]
        // AI-504 (Q8): the double-leaf 1500 mm is read from the name, not from a JKR source: listed, not failed.
        [InlineData("Double Flush", 1499.0, 2100, false)]
        [InlineData("Double Flush", 1499.4, 2100, false)]
        [InlineData("Double Flush", 1500.0, 2100, false)]
        [InlineData("Double Flush", 899.0, 2100, true)]
        [InlineData("Single", 900.0, 2099.0, true)]
        [InlineData("Single", 900.0, 2099.4, true)]
        [InlineData("Single", 900.0, 2100.0, false)]
        public void A1_2_door_minimums(string family, double width, double height, bool fails) =>
            Assert.Equal(fails, Failed(new SpecModel { Types = { T("Doors", family, "D", width: width, height: height) } }, "A1.2"));

        // ── A1.3 main door: thickness ≥ 40 mm (judged to 0.1 mm) ──

        [Theory]
        [InlineData(39.0, true)]
        [InlineData(39.9, true)]
        [InlineData(39.96, false)]    // prints "40" (one decimal)
        [InlineData(40.0, false)]
        [InlineData(40.1, false)]
        public void A1_3_main_door_thickness_is_40_mm(double thk, bool fails) =>
            Assert.Equal(fails, Failed(new SpecModel { Types = { T("Doors", "Main Door", "Solid Core", thickness: thk) } }, "A1.3"));

        // ── A1.5 decorative fence ≥ 2400 mm ──

        [Theory]
        [InlineData(2399.0, true)]
        [InlineData(2399.4, true)]
        [InlineData(2399.5, false)]
        [InlineData(2400.0, false)]
        [InlineData(2400.6, false)]
        public void A1_5_fence_height_is_2400_mm(double h, bool fails) =>
            Assert.Equal(fails, Failed(new SpecModel { Types = { T("Railings", "Decorative Fence", "F", height: h) } }, "A1.5"));

        // ── A2.1 file name: ≥ 3 segments ──

        [Theory]
        [InlineData("jkrAR24.rvt", true)]
        [InlineData("jkrAR24_5a.rvt", true)]
        [InlineData("jkrAR24_5a_W00.rvt", false)]
        [InlineData("jkrAR24_5a_W00_x.rvt", false)]
        public void A2_1_three_segments_pass_two_fail(string file, bool fails) =>
            Assert.Equal(fails, Failed(new SpecModel { FileName = file }, "A2.1"));

        // ── A4.1 base point: the documented position is exact; any other offset is listed, never failed (AI-504) ──

        private static SpecModel BasePointAt(double offsetMm)
        {
            var m = new SpecModel { ProjectBasePoint = new SpecPoint { X = offsetMm, Y = 0 } };
            m.Grids.Add(new SpecGrid { Id = 1, Name = "A", Offset = 0, IsVertical = true, HasLine = true, X0 = 0, X1 = 0, Y0 = -50000, Y1 = 50000 });
            m.Grids.Add(new SpecGrid { Id = 2, Name = "1", Offset = 0, IsVertical = false, HasLine = true, X0 = -50000, X1 = 50000, Y0 = 0, Y1 = 0 });
            return m;
        }

        [Theory]
        [InlineData(0.0, false)]
        [InlineData(9.9, false)]
        [InlineData(10.0, false)]
        [InlineData(10.1, false)]     // AI-504 (Q2): no tolerance, no fail: listed for a person
        [InlineData(25.0, false)]
        public void A4_1_never_fails_an_offset_a_person_decides_it(double offset, bool fails) =>
            Assert.Equal(fails, Failed(BasePointAt(offset), "A4.1"));

        // ── B1 / C1.6.3 / Db.1: the LOD → LOi column ──

        [Theory]
        [InlineData(0, 0)]
        [InlineData(99, 0)]
        [InlineData(100, 100)]
        [InlineData(299, 200)]
        [InlineData(300, 300)]
        [InlineData(350, 300)]
        [InlineData(400, 400)]
        [InlineData(499, 400)]
        [InlineData(500, 500)]
        [InlineData(600, 500)]
        public void LOD_falls_under_the_LOi_column_at_or_below_it(int lod, int loi) =>
            Assert.Equal(loi, SpecPbData.LoiOf(lod));

        private static SpecType DbType(string param, string value)
        {
            var t = T("Doors", "Pintu", "(PT1p600a) 900 x 2100");
            t.Texts[param] = value;
            return t;
        }

        [Theory]
        [InlineData(50, false)]
        [InlineData(299, false)]   // LOi 200: a Door's Kod_Jenis_jkr_stt starts at LOi 300 (PB p.51)
        [InlineData(300, true)]
        [InlineData(350, true)]
        [InlineData(400, true)]
        [InlineData(450, true)]
        [InlineData(500, true)]
        public void Db_1_a_Doors_Kod_Jenis_is_judged_from_LOi_300(int lod, bool judged)
        {
            var m = new SpecModel { Lod = lod, Types = { DbType("Kod_Jenis_jkr_stt", "") } };
            Assert.Equal(judged, Run(m, "Db.1").Any(r => r.Status == "fail"));
        }

        [Fact]
        public void Db_1_names_the_LOi_column_not_the_raw_LOD()
        {
            var m = new SpecModel { Lod = 450, Types = { DbType("Kod_Jenis_jkr_stt", "") } };
            var f = Run(m, "Db.1").Single(r => r.Status == "fail");
            Assert.Contains("LOi 400", f.Note);
            Assert.DoesNotContain("LOi 450", f.Note);
            Assert.Contains("LOi 400", f.NoteEn);
            Assert.DoesNotContain("LOi 450", f.NoteEn);
        }

        // ── Dc.1 name number vs parameter: equal within ±1 mm ──

        [Theory]
        [InlineData(146.0, false)]
        [InlineData(147.0, true)]     // AI-504 (Q11): no ±1 mm, the name number equals the parameter
        [InlineData(145.0, true)]
        [InlineData(146.0000001, false)]  // a nanometre of feet → mm float noise is a conversion fix, not a tolerance
        [InlineData(146.001, true)]
        [InlineData(147.1, true)]
        [InlineData(144.9, true)]
        [InlineData(150.0, true)]
        public void Dc_1_name_number_equals_the_width_exactly(double width, bool fails) =>
            Assert.Equal(fails, Failed(new SpecModel { Types = { T("Walls", "Basic Wall", "Wall 146", width: width) } }, "Dc.1"));
    }
}
