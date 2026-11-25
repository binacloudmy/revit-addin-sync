// read_dwg_plan (AI-607): the Revit-free reader. Drawings are built in memory as ACadSharp
// documents (a bare CadDocument carries the header unit), so the whole pipeline runs
// without a DWG on disk: units verdict, wall pairs, door from a swing, window from marks
// across a wall, plan listing with level text, the size budget and the no-DWG answer.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using BinaVibe.Copilot.Contracts;
using CSMath;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class DwgPlanReaderTests
    {
        private static readonly CadToBimSettings Defaults = new CadToBimSettings();

        private sealed class Drawing
        {
            public readonly CadDocument Doc = new CadDocument();
            private readonly Dictionary<string, Layer> _layers = new Dictionary<string, Layer>();

            public Drawing(UnitsType units) => Doc.Header.InsUnits = units;

            private Layer L(string name)
            {
                if (!_layers.TryGetValue(name, out var layer))
                {
                    layer = new Layer(name);
                    Doc.Layers.Add(layer);
                    _layers[name] = layer;
                }
                return layer;
            }

            public void Line(string layer, double x1, double y1, double x2, double y2) =>
                Doc.Entities.Add(new Line { StartPoint = new XYZ(x1, y1, 0), EndPoint = new XYZ(x2, y2, 0), Layer = L(layer) });

            public void Rect(string layer, double x1, double y1, double x2, double y2)
            {
                Line(layer, x1, y1, x2, y1);
                Line(layer, x2, y1, x2, y2);
                Line(layer, x2, y2, x1, y2);
                Line(layer, x1, y2, x1, y1);
            }

            public void Swing(string layer, double cx, double cy, double radius) =>
                Doc.Entities.Add(new Arc { Center = new XYZ(cx, cy, 0), Radius = radius, StartAngle = 0, EndAngle = Math.PI / 2, Layer = L(layer) });

            public void Text(string value, double x, double y) =>
                Doc.Entities.Add(new TextEntity { Value = value, InsertPoint = new XYZ(x, y, 0), Height = 250, Layer = L("A-TEXT") });

            /// <summary>A 10 x 8 m one-room house at (ox, oy): 200 mm walls, a 900 door in the bottom wall,
            /// a 1200 window in the right wall, a room name, and a title and level under the plan.</summary>
            /// <summary>A door block (quarter swing of 1000 mm, hinge at the block origin, on layer "dr",
            /// which no door hint matches) inserted at (x, y) with this scale; xScale &lt; 0 mirrors it.</summary>
            public void DoorBlock(double x, double y, double xScale, double yScale)
            {
                var block = Doc.BlockRecords.TryGetValue("DOOR-1000", out var found) ? found : null;
                if (block == null)
                {
                    block = new BlockRecord("DOOR-1000");
                    block.Entities.Add(new Arc { Center = new XYZ(0, 0, 0), Radius = 1000, StartAngle = 0, EndAngle = Math.PI / 2, Layer = L("dr") });
                    Doc.BlockRecords.Add(block);
                }
                Doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(x, y, 0), XScale = xScale, YScale = yScale, Layer = L("dr") });
            }

            public void House(double ox, double oy, string title = "PELAN ARAS 1", string level = "A.L:45.650", bool door = true)
            {
                Rect("A-WALL", ox, oy, ox + 10000, oy + 8000);
                Rect("A-WALL", ox + 200, oy + 200, ox + 9800, oy + 7800);
                if (door) Swing("A-DOOR", ox + 3000, oy + 200, 900);
                foreach (double y in new[] { 3000.0, 3600.0, 4200.0 })
                    Line("A-WINDOW", ox + 9750, oy + y, ox + 10050, oy + y);
                Text("BILIK TIDUR", ox + 5000, oy + 4000);
                Text(title, ox + 2000, oy - 1500);
                Text(level, ox + 2000, oy - 2200);
            }
        }

        private static JsonElement Data(Envelope e)
        {
            Assert.True(e.Ok, e.Error?.Message);
            return e.Data!.Value;
        }

        private static Envelope Read(Drawing d, params int[] plans) =>
            DwgPlanReader.Build(DwgPlanReader.Read(d.Doc, Defaults), plans);

        // ── units ─────────────────────────────────────────────────────────

        [Fact]
        public void Units_a_mm_header_on_a_building_sized_sheet_is_plausible()
        {
            var d = new Drawing(UnitsType.Millimeters);
            d.House(0, 0);

            var units = Data(Read(d)).GetProperty("units");

            Assert.True(units.GetProperty("plausible").GetBoolean());
            Assert.False(units.GetProperty("needs_confirmation").GetBoolean());
            Assert.Equal(1.0, units.GetProperty("used_mm_per_unit").GetDouble());
            Assert.False(units.TryGetProperty("note", out _));
        }

        [Fact]
        public void Units_an_inch_header_on_a_mm_drawing_is_flagged_for_the_user_not_silently_fixed()
        {
            // elec.dwg style: drawn in mm, header says inches. Two houses 60 m apart make the
            // sheet about 2 km wide at 25.4 mm per unit, which is not a building.
            var d = new Drawing(UnitsType.Inches);
            d.House(0, 0);
            d.House(60000, 0);

            Envelope e = Read(d);
            var units = Data(e).GetProperty("units");

            Assert.False(units.GetProperty("plausible").GetBoolean());
            Assert.True(units.GetProperty("needs_confirmation").GetBoolean());
            Assert.Equal(25.4, units.GetProperty("header_mm_per_unit").GetDouble());
            Assert.Equal(1.0, units.GetProperty("used_mm_per_unit").GetDouble());
            Assert.Contains("confirm", units.GetProperty("note").GetString());
            Assert.Contains("units need confirming", e.Summary);
            Assert.Contains("unit", e.Next);
        }

        [Fact]
        public void Units_verdict_without_a_header_unit_asks_too()
        {
            var v = DwgPlanReader.UnitsVerdict("Unitless", null, 1.0);
            Assert.False((bool)v["plausible"]);
            Assert.True((bool)v["needs_confirmation"]);
            Assert.False(v.ContainsKey("header_mm_per_unit"));
        }

        // ── walls, doors, windows ─────────────────────────────────────────

        [Fact]
        public void Wall_pairs_200mm_apart_become_four_200mm_walls()
        {
            var d = new Drawing(UnitsType.Millimeters);
            d.House(0, 0);

            var plan = Data(Read(d, 1)).GetProperty("plans")[0];
            var walls = plan.GetProperty("walls");
            var thickest = walls.GetProperty("by_thickness_mm")[0];

            Assert.Equal(4, walls.GetProperty("count").GetInt32());
            Assert.Equal(200, thickest.GetProperty("mm").GetDouble());
            Assert.Equal(4, thickest.GetProperty("count").GetInt32());
        }

        [Fact]
        public void Door_width_comes_from_the_swing_radius()
        {
            var d = new Drawing(UnitsType.Millimeters);
            d.House(0, 0);

            var doors = Data(Read(d, 1)).GetProperty("plans")[0].GetProperty("doors");

            Assert.Equal(1, doors.GetProperty("count").GetInt32());
            Assert.Equal(900, doors.GetProperty("widths_mm")[0].GetProperty("mm").GetDouble());
        }

        [Fact]
        public void Detail_lists_the_assumptions_and_the_unconfirmed_unit_first()
        {
            var mm = new Drawing(UnitsType.Millimeters);
            mm.House(0, 0);
            var ok = Data(Read(mm, 1)).GetProperty("assumptions").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("Door height 2100 mm", ok);
            Assert.DoesNotContain(ok, a => a!.Contains("not confirmed"));

            var inches = new Drawing(UnitsType.Inches);
            inches.House(0, 0);
            inches.House(60000, 0);
            var flagged = Data(Read(inches, 1)).GetProperty("assumptions")[0].GetString();
            Assert.Contains("not confirmed", flagged);
        }

        [Fact]
        public void Door_blocks_are_read_inside_inserts_at_scale_1_and_mirrored_at_0_9()
        {
            var d = new Drawing(UnitsType.Millimeters);
            d.House(0, 0, door: false);
            d.DoorBlock(3000, 200, 1, 1);        // 1000 wide
            d.DoorBlock(7000, 200, -0.9, 0.9);   // flipped to hinge the other way, 900 wide

            var doors = Data(Read(d, 1)).GetProperty("plans")[0].GetProperty("doors");
            var widths = doors.GetProperty("widths_mm").EnumerateArray()
                .Select(w => w.GetProperty("mm").GetDouble()).OrderBy(w => w).ToArray();

            Assert.Equal(2, doors.GetProperty("count").GetInt32());
            Assert.Equal(new[] { 900.0, 1000.0 }, widths);
        }

        [Fact]
        public void Door_hinged_on_a_wall_stub_is_as_wide_as_its_swing_not_the_stub()
        {
            // The wall stops at the doorway (x 1000 to 1900); the swing hinges on the stub's end.
            var d = new Drawing(UnitsType.Millimeters);
            d.Line("A-WALL", 0, 0, 1000, 0);
            d.Line("A-WALL", 0, 200, 1000, 200);
            d.Line("A-WALL", 1900, 0, 6000, 0);
            d.Line("A-WALL", 1900, 200, 6000, 200);
            d.Swing("dr", 1000, 200, 900);

            var doors = Data(Read(d, 1)).GetProperty("plans")[0].GetProperty("doors");

            Assert.Equal(1, doors.GetProperty("count").GetInt32());
            Assert.Equal(900, doors.GetProperty("widths_mm")[0].GetProperty("mm").GetDouble());
        }

        [Fact]
        public void Window_width_comes_from_the_marks_across_the_wall()
        {
            var d = new Drawing(UnitsType.Millimeters);
            d.House(0, 0);

            var windows = Data(Read(d, 1)).GetProperty("plans")[0].GetProperty("windows");

            Assert.Equal(1, windows.GetProperty("count").GetInt32());
            Assert.Equal(1200, windows.GetProperty("widths_mm")[0].GetProperty("mm").GetDouble());
        }

        [Fact]
        public void A_closed_house_reports_its_room_and_a_closed_outline()
        {
            var d = new Drawing(UnitsType.Millimeters);
            d.House(0, 0);

            var plan = Data(Read(d, 1)).GetProperty("plans")[0];

            Assert.True(plan.GetProperty("outline_closed").GetBoolean());
            Assert.Contains("BILIK TIDUR", plan.GetProperty("rooms").GetProperty("named").EnumerateArray().Select(x => x.GetString()));
        }

        // ── plan listing ──────────────────────────────────────────────────

        [Fact]
        public void Listing_shows_each_plan_with_its_title_level_and_elevation()
        {
            var d = new Drawing(UnitsType.Millimeters);
            d.House(0, 0, "PELAN ARAS 1", "A.L:45.650");
            d.House(30000, 0, "PELAN ARAS BAWAH", "A.L:42.150");

            Envelope e = Read(d);
            var data = Data(e);
            var rows = data.GetProperty("plans").EnumerateArray().ToList();

            Assert.Equal(2, data.GetProperty("plans_found").GetInt32());
            Assert.Equal(2, rows.Count);
            var aras1 = rows.Single(r => r.GetProperty("title").GetString() == "PELAN ARAS 1");
            Assert.Equal("ARAS 1", aras1.GetProperty("level").GetString());
            Assert.Equal("A.L:45.650", aras1.GetProperty("elevation").GetString());
            Assert.Equal("9.8 x 7.8", aras1.GetProperty("size_m").GetString());   // wall centrelines, 100 mm in from the outer face
            Assert.Contains("2 plans", e.Summary);
            Assert.Contains(data.GetProperty("layers").EnumerateArray(), l => l.GetProperty("name").GetString() == "A-WALL" && l.GetProperty("role").GetString() == "wall");
        }

        [Fact]
        public void Listing_of_a_40_plan_sheet_stays_within_the_size_budget()
        {
            var d = new Drawing(UnitsType.Millimeters);
            for (int i = 0; i < 40; i++) d.House((i % 8) * 30000, (i / 8) * 30000, "PELAN ARAS " + (i + 1));

            Envelope e = Read(d);
            var data = Data(e);
            string json = JsonSerializer.Serialize(e.Data);

            Assert.Equal(40, data.GetProperty("plans_found").GetInt32());
            Assert.True(json.Length < 12000, "listing is " + json.Length + " chars");
            Assert.True(e.Summary.Length <= 300);
        }

        [Fact]
        public void Asking_for_a_plan_that_is_not_there_names_the_ones_that_are()
        {
            var d = new Drawing(UnitsType.Millimeters);
            d.House(0, 0);

            Envelope e = Read(d, 7);

            Assert.False(e.Ok);
            Assert.Equal(ErrorCodes.NotFound, e.Error!.Code);
            Assert.Contains("plans 1-1", e.Error.Message);
        }

        [Theory]
        [InlineData("n_wall", "wall")]
        [InlineData("nwall", "wall")]
        [InlineData("AS - WALL", "wall")]
        [InlineData("n_door & window", "door_or_window")]
        [InlineData("A-DOOR", "door")]
        [InlineData("A-GLAZ", "window")]
        [InlineData("A-FURN", "other")]
        public void Layer_roles_follow_the_name_hints(string layer, string role) =>
            Assert.Equal(role, DwgPlanReader.RoleOf(layer, Defaults).Role);

        [Theory]
        [InlineData("PELAN LANTAI ARAS 1 (BANGUNAN SEKOLAH UTAMA) SCALE 1:150", "ARAS 1")]
        [InlineData("PELAN ARAS BAWAH", "ARAS BAWAH")]
        [InlineData("GROUND FLOOR PLAN", "GROUND FLOOR")]
        public void Level_text_prefers_the_aras_number(string title, string level) =>
            Assert.Equal(level, DwgPlanReader.LevelText(new[] { title }).Level);

        [Fact]
        public void Elevation_text_is_read_from_al_and_ffl_labels()
        {
            Assert.Equal("A.L:45.650", DwgPlanReader.LevelText(new[] { "A.L:45.650" }).Elevation);
            Assert.Equal("F.F.L7.00", DwgPlanReader.LevelText(new[] { "F.F.L 7.00" }).Elevation);
        }

        // ── arguments and no DWG ──────────────────────────────────────────

        [Fact]
        public void No_dwg_ref_answers_attach_a_dwg_first()
        {
            Assert.True(DwgPlanReader.TryParseArgs(JsonDocument.Parse("{}").RootElement, out var a, out _));
            Assert.Null(a.DwgRef);

            Envelope e = DwgPlanReader.NoDwg();

            Assert.False(e.Ok);
            Assert.Equal(ErrorCodes.NotFound, e.Error!.Code);
            Assert.StartsWith("Attach a DWG first", e.Error.Message);
        }

        [Fact]
        public void A_model_ref_with_no_model_open_says_so()
        {
            Assert.Equal(DwgPlanReader.NoModelMessage, DwgPlanReader.ModelRefProblem("model:312045", modelOpen: false, out _));
            Assert.Null(DwgPlanReader.ModelRefProblem("model:312045", modelOpen: true, out long id));
            Assert.Equal(312045, id);
            Assert.StartsWith("dwg_ref must be", DwgPlanReader.ModelRefProblem("drawing.dwg", modelOpen: true, out _));
        }

        [Theory]
        [InlineData("{\"plans\":[1,2,3,4]}")]
        [InlineData("{\"plans\":[0]}")]
        [InlineData("{\"plan\":[1]}")]
        [InlineData("{\"dwg_ref\":5}")]
        public void Bad_arguments_are_refused(string json) =>
            Assert.False(DwgPlanReader.TryParseArgs(JsonDocument.Parse(json).RootElement, out _, out _));

        /// <summary>Headless run on a real file: set AI605_DWG (and optionally AI605_PLANS="1,7", AI605_OUT=path).
        /// Does nothing when AI605_DWG is unset, so CI is unaffected.</summary>
        [Fact]
        public void Headless_read_of_a_real_dwg_when_AI605_DWG_is_set()
        {
            string? path = Environment.GetEnvironmentVariable("AI605_DWG");
            if (string.IsNullOrEmpty(path)) return;
            int[] plans = (Environment.GetEnvironmentVariable("AI605_PLANS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

            Envelope e = DwgPlanReader.Build(DwgPlanReader.ReadFile(path, Defaults), plans);
            string json = JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true });
            string? output = Environment.GetEnvironmentVariable("AI605_OUT");
            if (!string.IsNullOrEmpty(output)) System.IO.File.WriteAllText(output, json);
            Assert.True(e.Ok || e.Error != null, json);
        }

        [Fact]
        public void Good_arguments_are_read()
        {
            Assert.True(DwgPlanReader.TryParseArgs(JsonDocument.Parse("{\"dwg_ref\":\"att:abc\",\"plans\":[2,5]}").RootElement, out var a, out _));
            Assert.Equal("att:abc", a.DwgRef);
            Assert.Equal(new[] { 2, 5 }, a.Plans);
        }
    }
}
