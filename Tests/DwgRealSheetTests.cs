// AI-610: read_dwg_plan (DwgPlanReader) on two real DWGs, pinned as read on 2026-11-26.
// The drawings are not in the repo. Set AI610_ASWARA_DWG (01_FLOOR PLAN_ASRAMA ASWARA.dwg)
// and AI610_ELEC_DWG (elec.dwg); a test whose file is unset or missing returns early and
// passes, the same way the AI605_DWG test does (xunit 2 has no runtime skip). Each file is
// read once per run, about 4-5 s each.
//
// The [Fact(Skip=...)] tests are known limits (AI-610), not fixes: each asserts the desired
// behaviour, so removing the Skip shows whether a later change fixed it.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services.CadToBim;
using Xunit;
using Xunit.Abstractions;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class DwgRealSheetTests
    {
        private const string AswaraVar = "AI610_ASWARA_DWG";
        private const string ElecVar = "AI610_ELEC_DWG";

        private static readonly Dictionary<string, DwgPlanSheet> Sheets = new Dictionary<string, DwgPlanSheet>();
        private readonly ITestOutputHelper _out;

        public DwgRealSheetTests(ITestOutputHelper output) => _out = output;

        /// <summary>The sheet read from the file the variable names, or null (with the reason logged) to skip.</summary>
        private DwgPlanSheet? Sheet(string variable)
        {
            string? path = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(path)) { _out.WriteLine("skipped: " + variable + " is not set"); return null; }
            if (!File.Exists(path)) { _out.WriteLine("skipped: " + variable + " names a missing file: " + path); return null; }
            lock (Sheets)
            {
                if (!Sheets.TryGetValue(path, out DwgPlanSheet? sheet))
                    Sheets[path] = sheet = DwgPlanReader.ReadFile(path, new CadToBimSettings());
                return sheet;
            }
        }

        private static JsonElement Data(Envelope e)
        {
            Assert.True(e.Ok, e.Error?.Message);
            return JsonSerializer.SerializeToElement(e.Data);
        }

        private static List<JsonElement> Rows(DwgPlanSheet sheet) =>
            Data(DwgPlanReader.Build(sheet, Array.Empty<int>())).GetProperty("plans").EnumerateArray().ToList();

        private static string? Str(JsonElement row, string key) =>
            row.TryGetProperty(key, out JsonElement v) ? v.GetString() : null;

        private static JsonElement ByTitle(List<JsonElement> rows, string title) => rows.Single(r => Str(r, "title") == title);

        // ── plan clustering ──────────────────────────────────────────────

        [Fact]
        public void Aswara_lists_8_plans_and_skips_15_details_without_a_warning()
        {
            DwgPlanSheet? sheet = Sheet(AswaraVar);
            if (sheet == null) return;

            JsonElement data = Data(DwgPlanReader.Build(sheet, Array.Empty<int>()));
            List<JsonElement> rows = data.GetProperty("plans").EnumerateArray().ToList();

            Assert.Equal(8, data.GetProperty("plans_found").GetInt32());
            Assert.Equal(15, data.GetProperty("details_skipped").GetInt32());
            Assert.False(data.TryGetProperty("warnings", out _));
            // Listing order as observed (cluster order, n 1-8).
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, rows.Select(r => r.GetProperty("n").GetInt32()));
            Assert.Equal(
                new[] { "ARAS 2 (ASRAMA213 ORG)", "ARAS 1", "ARAS 3 (ASRAMA 213 ORANG)", "ARAS 4 (ASRAMA 213 ORANG)", "ARAS RUANG SERVIS", "PELAN BUMBUNG", null, "ARAS 1A" },
                rows.Select(r => Str(r, "title")));
            Assert.Equal(new[] { 351, 277, 493, 405, 126, 48, 23, 96 }, rows.Select(r => r.GetProperty("walls").GetInt32()));
        }

        // ── units guard ──────────────────────────────────────────────────

        [Fact]
        public void Aswara_units_are_millimetres_plausible_and_need_no_confirmation()
        {
            DwgPlanSheet? sheet = Sheet(AswaraVar);
            if (sheet == null) return;

            JsonElement units = Data(DwgPlanReader.Build(sheet, Array.Empty<int>())).GetProperty("units");

            Assert.Equal("Millimeters", units.GetProperty("header").GetString());
            Assert.True(units.GetProperty("plausible").GetBoolean());
            Assert.False(units.GetProperty("needs_confirmation").GetBoolean());
        }

        [Fact]
        public void Elec_inch_header_is_flagged_for_confirmation()
        {
            DwgPlanSheet? sheet = Sheet(ElecVar);
            if (sheet == null) return;

            JsonElement units = Data(DwgPlanReader.Build(sheet, Array.Empty<int>())).GetProperty("units");

            Assert.Equal("Inches", units.GetProperty("header").GetString());
            Assert.False(units.GetProperty("plausible").GetBoolean());
            Assert.True(units.GetProperty("needs_confirmation").GetBoolean());
            Assert.Equal(1.0, units.GetProperty("used_mm_per_unit").GetDouble());
        }

        // ── level text ───────────────────────────────────────────────────

        [Fact]
        public void Aswara_levels_and_al_elevations_read_in_listing_order()
        {
            DwgPlanSheet? sheet = Sheet(AswaraVar);
            if (sheet == null) return;

            List<JsonElement> rows = Rows(sheet);

            // "ARAS RUANG SERVIS" has no level: the level pattern wants ARAS + number/BAWAH/BUMBUNG/ATAS.
            Assert.Equal(new[] { "ARAS 2", "ARAS 1", "ARAS 3", "ARAS 4", null, "BUMBUNG", null, "ARAS 1A" }, rows.Select(r => Str(r, "level")));
            Assert.Equal(
                new[] { "A.L:53.375", "A.L:43.200", "A.L:55.125", "A.L:57.750", "A.L:61.150", "A.L:66.000", null, "A.L:44.640" },
                rows.Select(r => Str(r, "elevation")));
        }

        [Fact]
        public void Elec_old_plan_8_splits_into_three_floors_with_their_ffl()
        {
            DwgPlanSheet? sheet = Sheet(ElecVar);
            if (sheet == null) return;

            List<JsonElement> rows = Rows(sheet);
            const string T = " (BANGUNAN SEKOLAH UTAMA) SCALE 1:150";
            JsonElement bawah = ByTitle(rows, "PELAN LANTAI ARAS BAWAH" + T);
            JsonElement aras1 = ByTitle(rows, "PELAN LANTAI ARAS 1" + T);
            JsonElement aras2 = ByTitle(rows, "PELAN LANTAI ARAS 2" + T + " (2)");

            Assert.Equal(("ARAS BAWAH", "F.F.L0.100"), (Str(bawah, "level"), Str(bawah, "elevation")));
            Assert.Equal(("ARAS 1", "F.F.L3.80"), (Str(aras1, "level"), Str(aras1, "elevation")));
            Assert.Equal(("ARAS 2", "F.F.L7.00"), (Str(aras2, "level"), Str(aras2, "elevation")));
            // One stack on the sheet, bottom to top: same column, rising centres.
            double[] y = new[] { bawah, aras1, aras2 }.Select(r => double.Parse(Str(r, "centre_m")!.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.True(y[0] < y[1] && y[1] < y[2], string.Join(", ", y));
        }

        // ── classifier baseline ──────────────────────────────────────────

        /// <summary>
        /// Baseline pin, not a statement that these picks are right: the wall/door/window layer roles
        /// and opening counts as read_dwg_plan makes them today ("n_low wall", "color wall" and
        /// "n_hatching retaining wall" read as walls). A classifier change that moves these should
        /// update this test on purpose.
        /// </summary>
        [Fact]
        public void Aswara_classifier_baseline_layer_picks_and_counts()
        {
            DwgPlanSheet? sheet = Sheet(AswaraVar);
            if (sheet == null) return;

            var picks = Data(DwgPlanReader.Build(sheet, Array.Empty<int>())).GetProperty("layers").EnumerateArray()
                .Where(l => l.GetProperty("role").GetString() != "other")
                .Select(l => l.GetProperty("role").GetString() + ":" + l.GetProperty("name").GetString() + ":" + l.GetProperty("segments").GetInt32())
                .ToList();
            Assert.Equal(new[]
            {
                "wall:n_wall:5720", "door:Door:4783", "door_or_window:n_door & window:4313", "wall:n_low wall:2013",
                "wall:color wall:1923", "wall:nwall:1022", "wall:colorpartition:217", "wall:AS - WALL:108",
                "wall:WALL 150MM:61", "wall:n_hatching retaining wall:21", "window:window:15", "wall:AS - STAIR&WALL:11", "wall:WALL:5",
            }, picks);

            List<DwgPlan> listed = sheet.Plans.Where(p => p.N <= 8).ToList();
            _out.WriteLine("walls/doors/windows per plan: " + string.Join(" | ", listed.Select(p => p.Cluster.Walls.Count + "/" + p.Doors.Count + "/" + p.Windows.Count)));
            Assert.Equal(new[] { 351, 277, 493, 405, 126, 48, 23, 96 }, listed.Select(p => p.Cluster.Walls.Count));
            Assert.Equal(BaselineDoors, listed.Select(p => p.Doors.Count));
            Assert.Equal(BaselineWindows, listed.Select(p => p.Windows.Count));
        }

        private static readonly int[] BaselineDoors = { 26, 21, 28, 28, 7, 0, 0, 9 };
        private static readonly int[] BaselineWindows = { 20, 4, 25, 23, 3, 0, 0, 2 };

        // ── known limits (AI-610): skipped, each asserts the desired behaviour ──

        [Fact(Skip = "Known limit AI-610: the plan-not-found message counts every cluster (\"plans 1-69\" on ASWARA) while 8 are listed.")]
        public void Known_limit_plan_not_found_names_only_the_listed_plans()
        {
            DwgPlanSheet? sheet = Sheet(AswaraVar);
            if (sheet == null) return;

            Envelope e = DwgPlanReader.Build(sheet, new[] { 99 });

            Assert.False(e.Ok);
            Assert.DoesNotContain("1-" + sheet.Plans.Count, e.Error!.Message);
            Assert.Contains("plans 1-8", e.Error.Message);
        }

        [Fact(Skip = "Known limit AI-610: elec plan 1 is untitled while its repeat is \"ARAS BAWAH (2)\"; the first of a repeated name gets no title.")]
        public void Known_limit_elec_first_of_a_repeated_level_is_titled_too()
        {
            DwgPlanSheet? sheet = Sheet(ElecVar);
            if (sheet == null) return;

            List<JsonElement> rows = Rows(sheet);

            Assert.Equal("ARAS BAWAH (2)", Str(rows[1], "title"));
            Assert.Equal("ARAS BAWAH", Str(rows[0], "title"));
        }

        [Fact(Skip = "Known limit AI-610: elec plan 1 is 125 m tall against 14-17 m floors, so it probably still holds stacked floors.")]
        public void Known_limit_elec_plan_1_is_one_floor_tall()
        {
            DwgPlanSheet? sheet = Sheet(ElecVar);
            if (sheet == null) return;

            DwgPlan first = sheet.Plans.Single(p => p.N == 1);

            Assert.True(first.Cluster.Height < 20000, "plan 1 is " + first.Cluster.Height / 1000 + " m tall");
        }
    }
}
