using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RevitWebAppSync.UI.SpacePlanning.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The massing/space-planning contract, pinned without Revit or a live backend:
    ///   1. the frozen /planning/suggest payload deserializes field-for-field
    ///   2. the metres→millimetres args-builder converts exactly once
    ///   3. the preview canvas's auto-fit transform math
    ///   4. the room-type palette is complete and stable
    /// </summary>
    public class MassingPlanTests
    {
        private static SuggestResult Sample()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "data", "planning-suggest-sample.json");
            return JsonConvert.DeserializeObject<SuggestResult>(File.ReadAllText(path));
        }

        // ── 1. wire contract ────────────────────────────────────────────────

        [Fact]
        public void SamplePayload_Deserializes_SoaWithCitations()
        {
            var r = Sample();

            Assert.True(r.Success);
            Assert.Null(r.Error);
            Assert.Equal(6, r.Soa.Items.Count);
            Assert.Equal(2372.4, r.Soa.TotalGfaM2, 3);

            // The tandas ROW is sized by the JKR module (it's a toilet-block bay).
            // The UBBL requirement is the FIXTURE COUNTS in soa.sanitary[] — asserted
            // separately below. The spec's original sample cited UBBL on this row;
            // the backend owner confirmed 2026-07-30 that the backend is correct and
            // the spec sample was stale, so this fixture was corrected to match.
            var tandas = r.Soa.Items.Single(i => i.Key == "tandas");
            Assert.Equal("Blok Tandas", tandas.LabelMs);
            Assert.Equal(4, tandas.Count);
            Assert.Equal(259.2, tandas.TotalAreaM2, 3);
            Assert.Equal("JKR Sekolah Modul", tandas.Source);
            Assert.Equal("toilet block bay", tandas.Clause);
            Assert.True(tandas.Advisory);
            Assert.Equal("JKR Sekolah Modul · toilet block bay", tandas.Citation);

            // Single `level` is null for a space that SPANS storeys; `levels` carries
            // the real answer and LevelLabel collapses it for display.
            Assert.Null(tandas.Level);
            Assert.Equal(new[] { 1, 2 }, tandas.Levels);
            Assert.Equal("Tingkat 1–2", tandas.LevelLabel);
            Assert.Equal(1, r.Soa.Items.Single(i => i.Key == "kantin").Level);
        }

        [Fact]
        public void SamplePayload_Deserializes_SanitaryBreakdown()
        {
            var soa = Sample().Soa;

            Assert.Equal(4, soa.Sanitary.Count);
            var femaleWc = soa.Sanitary.Single(f => f.Fixture == "wc" && f.Gender == "female");
            Assert.Equal(11, femaleWc.Count);
            Assert.Equal("UBBL 1984", femaleWc.Source);
            Assert.Contains("Eighth Schedule", femaleWc.Clause);
            Assert.Contains("advisory", soa.Notes);
        }

        [Fact]
        public void SamplePayload_Deserializes_SchemeRoomsAndStringKeyedLevelAreas()
        {
            var scheme = Sample().Schemes.Single();

            Assert.Equal("A", scheme.Id);
            Assert.Equal("Dua Blok Selari", scheme.Title);
            Assert.True(scheme.MeetsGfa);
            Assert.Equal(590.4, scheme.MarginM2, 3);
            Assert.Empty(scheme.Warnings);

            // JSON object keys are strings ("1"/"2") — LevelArea bridges that.
            Assert.Equal(1731.6, scheme.LevelArea(1), 3);
            Assert.Equal(1231.2, scheme.LevelArea(2), 3);
            Assert.Equal(0.0, scheme.LevelArea(3));           // absent → 0, not a throw
            Assert.Equal(new[] { 1, 2 }, scheme.Levels());

            var kelas = scheme.Rooms.First(x => x.Label == "1A");
            Assert.Equal("kelas", kelas.Type);
            Assert.Equal(6.0, kelas.X, 3);
            Assert.Equal(7.2, kelas.W, 3);
            Assert.Equal(9.0, kelas.H, 3);
            Assert.Equal(1, kelas.Level);
            Assert.True(kelas.CountsAsGfa);

            // The field is drawn but is not floor area.
            Assert.False(scheme.Rooms.Single(x => x.Type == "padang").CountsAsGfa);
        }

        [Fact]
        public void SamplePayload_Deserializes_RejectedAndStats()
        {
            var r = Sample();

            var rejected = r.Rejected.Single();
            Assert.Equal("A1", rejected.Id);
            Assert.Equal(640.8, rejected.GapM2, 3);
            Assert.Equal("below_target_gfa", rejected.Reason);
            Assert.Equal("below target gfa", rejected.ReasonLabel);

            Assert.Equal(6, r.Stats.SchemeCount);
            Assert.Equal(3, r.Stats.PassingCount);
            Assert.Equal(1158.5, r.Stats.BestMarginM2.Value, 3);
        }

        [Fact]
        public void FailurePayload_StaysTyped()
        {
            var r = JsonConvert.DeserializeObject<SuggestResult>(
                "{\"success\":false,\"error\":\"upstream timeout\",\"schemes\":[]}");

            Assert.False(r.Success);
            Assert.Equal("upstream timeout", r.Error);
            Assert.Empty(r.Schemes);      // never null — the UI enumerates it
            Assert.Empty(r.Rejected);
        }

        [Fact]
        public void Request_SerializesToSnakeCase()
        {
            var json = JsonConvert.SerializeObject(
                new SuggestRequest { Brief = "sekolah rendah", UserId = 123 });

            Assert.Contains("\"brief\":\"sekolah rendah\"", json);
            Assert.Contains("\"user_id\":123", json);
            Assert.Contains("\"target_gfa_m2\":null", json);
            Assert.DoesNotContain("\"Brief\"", json);
        }

        // ── 2. metres → millimetres, exactly once ───────────────────────────

        [Fact]
        public void Build_ConvertsMetresToMillimetres_Once()
        {
            var scheme = Sample().Schemes.Single();
            var args = MassingArgs.Build(scheme);

            var rooms = (List<object>)args["rooms"];
            var kelas = rooms.Cast<Dictionary<string, object>>().First(r => (string)r["label"] == "1A");
            var boundary = ((List<object>)kelas["boundary_mm"]).Cast<List<object>>()
                .Select(p => new { X = (double)p[0], Y = (double)p[1] }).ToList();

            // 7.2 m × 9.0 m at (6,6) → 7200 mm × 9000 mm at (6000,6000).
            // A missing or doubled ×1000 shows up here as 7.2 or 7_200_000.
            Assert.Equal(4, boundary.Count);
            Assert.Equal(6000.0, boundary[0].X, 6);
            Assert.Equal(6000.0, boundary[0].Y, 6);
            Assert.Equal(13200.0, boundary[1].X, 6);
            Assert.Equal(6000.0, boundary[1].Y, 6);
            Assert.Equal(13200.0, boundary[2].X, 6);
            Assert.Equal(15000.0, boundary[2].Y, 6);
            Assert.Equal(6000.0, boundary[3].X, 6);
            Assert.Equal(15000.0, boundary[3].Y, 6);

            Assert.Equal(3000.0, (double)kelas["height_mm"], 6);
            Assert.Equal(1, (int)kelas["level"]);
        }

        [Fact]
        public void Build_DropsSiteOnlyRooms()
        {
            var scheme = Sample().Schemes.Single();
            var rooms = ((List<object>)MassingArgs.Build(scheme)["rooms"])
                .Cast<Dictionary<string, object>>().ToList();

            // 5 rooms in, padang out — a 900 m² slab on the field would both
            // corrupt the GFA and drop a plate where the field belongs.
            Assert.Equal(4, rooms.Count);
            Assert.DoesNotContain("padang", rooms.Select(r => (string)r["type"]));
        }

        [Fact]
        public void Build_EmitsOneLevelSpecPerStorey()
        {
            var scheme = Sample().Schemes.Single();
            var levels = ((List<object>)MassingArgs.Build(scheme)["levels"])
                .Cast<Dictionary<string, object>>().ToList();

            Assert.Equal(2, levels.Count);
            Assert.Equal("Tingkat 1", levels[0]["name"]);
            Assert.Equal(0.0, (double)levels[0]["elevation_mm"], 6);
            Assert.Equal(1, (int)levels[0]["level"]);
            Assert.Equal("Tingkat 2", levels[1]["name"]);
            Assert.Equal(4000.0, (double)levels[1]["elevation_mm"], 6);
            Assert.Equal(2, (int)levels[1]["level"]);
        }

        [Fact]
        public void Build_LevelSpecsCarryTheirOwnIndex_ForSingleUpperStoreyScheme()
        {
            // Rooms only on level 2: position in the array is 0 but the level is 2,
            // which is why the mutator maps by the `level` value, not by position.
            var scheme = new MassingScheme
            {
                Id = "Z", Title = "Upper only",
                Rooms = new List<MassingRoom>
                {
                    new MassingRoom { Label = "2A", Type = "kelas", X = 0, Y = 0, W = 7.2, H = 9, Level = 2 },
                },
            };
            var levels = ((List<object>)MassingArgs.Build(scheme)["levels"])
                .Cast<Dictionary<string, object>>().Single();

            Assert.Equal(2, (int)levels["level"]);
            Assert.Equal("Tingkat 2", levels["name"]);
            Assert.Equal(4000.0, (double)levels["elevation_mm"], 6);
        }

        [Fact]
        public void Build_NamesTheGroupAfterTheScheme()
        {
            var args = MassingArgs.Build(Sample().Schemes.Single());
            Assert.Equal("Massing — Dua Blok Selari", args["option_name"]);
            Assert.Equal(false, args["make_walls"]);

            Assert.Equal(true, MassingArgs.Build(Sample().Schemes.Single(), makeWalls: true)["make_walls"]);
            Assert.Equal("custom", MassingArgs.Build(Sample().Schemes.Single(), optionName: "custom")["option_name"]);
        }

        [Fact]
        public void Build_RejectsNullScheme() =>
            Assert.Throws<ArgumentNullException>(() => MassingArgs.Build(null));

        // ── 3. preview auto-fit transform ───────────────────────────────────

        private static List<MassingRoom> TwoRooms() => new List<MassingRoom>
        {
            new MassingRoom { Label = "a", X = 0,  Y = 0,  W = 10, H = 10, Level = 1 },
            new MassingRoom { Label = "b", X = 10, Y = 10, W = 10, H = 10, Level = 1 },
        };

        [Fact]
        public void Fit_CentresAndScalesInsideTheSurface()
        {
            var fit = PlanFit.Fit(TwoRooms(), 200, 100);

            Assert.False(fit.IsEmpty);
            Assert.Equal(0, fit.MinX);
            Assert.Equal(20, fit.MaxX);
            // 20 m across a 200×100 box fits on the SHORT axis: 100/20 × 0.92.
            Assert.Equal(100.0 / 20 * 0.92, fit.Scale, 6);
            // Centred: equal gutters on the constrained axis.
            Assert.Equal((200 - 20 * fit.Scale) / 2, fit.MarginX, 6);
            Assert.Equal((100 - 20 * fit.Scale) / 2, fit.MarginY, 6);
        }

        [Fact]
        public void Fit_FlipsYSoNorthIsUp()
        {
            var fit = PlanFit.Fit(TwoRooms(), 200, 100);

            // The NORTHERN room (y=10) must draw ABOVE the southern one (smaller top).
            fit.RectOf(TwoRooms()[0], out _, out var southTop, out _, out _);
            fit.RectOf(TwoRooms()[1], out _, out var northTop, out _, out _);
            Assert.True(northTop < southTop,
                $"north room top {northTop} should be above south room top {southTop}");

            // y = MinY maps to the bottom edge of the fitted content.
            Assert.Equal(100 - fit.MarginY, fit.ToScreenY(0), 6);
        }

        [Fact]
        public void Fit_KeepsEveryRoomInsideTheSurface()
        {
            var rooms = Sample().Schemes.Single().Rooms.Where(r => r.Level == 1).ToList();
            var fit = PlanFit.Fit(rooms, 380, 260);

            foreach (var room in rooms)
            {
                fit.RectOf(room, out var left, out var top, out var w, out var h);
                Assert.True(left >= -0.001, $"{room.Label} left {left}");
                Assert.True(top >= -0.001, $"{room.Label} top {top}");
                Assert.True(left + w <= 380.001, $"{room.Label} right {left + w}");
                Assert.True(top + h <= 260.001, $"{room.Label} bottom {top + h}");
            }
        }

        [Fact]
        public void Fit_PreservesAspectRatio()
        {
            var fit = PlanFit.Fit(TwoRooms(), 400, 100);
            fit.RectOf(new MassingRoom { X = 0, Y = 0, W = 7.2, H = 9.0 }, out _, out _, out var w, out var h);
            Assert.Equal(7.2 / 9.0, w / h, 6);
        }

        [Fact]
        public void Fit_HandlesDegenerateInput()
        {
            Assert.True(PlanFit.Fit(null, 100, 100).IsEmpty);
            Assert.True(PlanFit.Fit(new List<MassingRoom>(), 100, 100).IsEmpty);
            Assert.True(PlanFit.Fit(TwoRooms(), 0, 100).IsEmpty);

            // A zero-height row must fit on X alone instead of dividing by zero.
            var flat = PlanFit.Fit(
                new List<MassingRoom> { new MassingRoom { X = 0, Y = 5, W = 10, H = 0 } }, 200, 100);
            Assert.False(flat.IsEmpty);
            Assert.Equal(200.0 / 10 * 0.92, flat.Scale, 6);
            Assert.False(double.IsNaN(flat.MarginY));
        }

        // ── 4. palette ──────────────────────────────────────────────────────

        [Theory]
        [InlineData("kelas")]
        [InlineData("sokongan")]
        [InlineData("tandas")]
        [InlineData("perhimpunan")]
        [InlineData("kantin")]
        [InlineData("padang")]
        [InlineData("selasar")]
        public void Palette_CoversEveryContractRoomType(string type)
        {
            var sw = MassingPalette.For(type);
            Assert.Equal(type, sw.Type);
            foreach (var hex in new[] { sw.Fill, sw.Stroke, sw.FillDark, sw.StrokeDark })
                Assert.Matches("^#[0-9A-Fa-f]{6,8}$", hex);
        }

        [Fact]
        public void Palette_UnknownTypeIsStableAcrossCalls()
        {
            // Must NOT use string.GetHashCode (randomized per process) — otherwise a
            // scheme recolours on every Revit restart.
            var first = MassingPalette.For("surau");
            Assert.Same(first, MassingPalette.For("surau"));
            Assert.Same(MassingPalette.For(null), MassingPalette.For(""));
            Assert.NotNull(first.Label);
        }

        // ── 5. captured live-backend response ───────────────────────────────
        // planning-suggest-live.json is a VERBATIM 200 from bina-ai
        // POST /planning/suggest (captured 2026-07-30, brief "sekolah rendah 18
        // bilik darjah, 2 tingkat"). The sample fixture above is hand-written from
        // the spec and so can only prove the addin agrees with the spec; these
        // prove it agrees with the server that actually shipped. If the backend
        // renames or retypes a field, this section fails instead of the pane
        // silently rendering zeroes.

        private static SuggestResult Live()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "data", "planning-suggest-live.json");
            return JsonConvert.DeserializeObject<SuggestResult>(File.ReadAllText(path));
        }

        [Fact]
        public void LivePayload_PopulatesEveryFieldTheScreenReads()
        {
            var r = Live();

            Assert.True(r.Success);
            Assert.Null(r.Error);

            // Every SOA row the screen prints must have arrived, not defaulted.
            Assert.Equal(6, r.Soa.Items.Count);
            Assert.Equal(2372.4, r.Soa.TotalGfaM2, 3);
            foreach (var i in r.Soa.Items)
            {
                Assert.False(string.IsNullOrWhiteSpace(i.Key));
                Assert.False(string.IsNullOrWhiteSpace(i.LabelMs));
                Assert.False(string.IsNullOrWhiteSpace(i.Citation));
                Assert.True(i.Count > 0);
            }
            Assert.Equal(4, r.Soa.Sanitary.Count);

            // Stats card.
            Assert.Equal(2372.4, r.Stats.TargetGfaM2, 3);
            Assert.Equal(6, r.Stats.SchemeCount);
            Assert.Equal(3, r.Stats.PassingCount);
            Assert.Equal(3, r.Stats.RejectedCount);

            // Scheme cards, and the string-keyed level areas the UI bridges by int.
            Assert.Equal(3, r.Schemes.Count);
            foreach (var s in r.Schemes)
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Id));
                Assert.False(string.IsNullOrWhiteSpace(s.Title));
                Assert.NotEmpty(s.Rooms);
                Assert.True(s.TotalGfaM2 > 0);
                Assert.True(s.MeetsGfa);
                Assert.Equal(new[] { 1, 2 }, s.Levels());
                Assert.True(s.LevelArea(1) > 0);
                Assert.True(s.LevelArea(2) > 0);
            }

            // Rejected list.
            Assert.Equal(3, r.Rejected.Count);
            foreach (var x in r.Rejected)
            {
                Assert.False(string.IsNullOrWhiteSpace(x.Id));
                Assert.Equal("below target gfa", x.ReasonLabel);
                Assert.True(x.GapM2 > 0);
            }
        }

        [Fact]
        public void BuildArgs_DeclareTheLod100Contract()
        {
            // The deliverable is LOD 100: generic conceptual masses, NOT floors or
            // walls. These args are what makes that true downstream, so pin them —
            // silently flipping make_walls on, or dropping storey_height_mm (which
            // makes the masses stack flush into a readable form), would change the
            // deliverable's LOD without anything failing.
            var args = MassingArgs.Build(Live().Schemes[0]);

            Assert.Equal("LOD 100", args["lod"]);
            Assert.Equal(MassingArgs.StoreyHeightMm, (double)args["storey_height_mm"]);
            Assert.Equal(4000.0, (double)args["storey_height_mm"]);
            Assert.False((bool)args["make_walls"]);   // walls would be LOD 200

            // Every scheme the backend emits starts at the same origin, so without
            // this two Builds land exactly on top of each other — which is also the
            // only way to model a programme above the generator's ~3,530 m² ceiling
            // (several briefs placed side by side).
            Assert.True((bool)args["auto_offset"]);
            Assert.False((bool)MassingArgs.Build(Live().Schemes[0], autoOffset: false)["auto_offset"]);
        }

        [Fact]
        public void LivePayload_CarriesTheProgramReadFields()
        {
            // Added by the backend 2026-07-30. site/setback are echoed from the
            // request (or parsed out of the brief text); the captured fixture was
            // requested with site_area_m2=5800, setback_m=6.
            var r = Live();

            Assert.Equal(5800, r.SiteAreaM2);
            Assert.Equal(6, r.SetbackM);
            Assert.Equal(2372.4, r.TargetGfaM2.Value, 3);
            Assert.Equal("sekolah_rendah", r.BuildingType);
            Assert.Equal("Sekolah rendah", r.BuildingTypeLabel);
        }

        [Fact]
        public void ProgramReadFields_AreNullableSoChipsCanBeHidden()
        {
            // A brief that states no site must leave these null rather than 0 —
            // the screen hides the chip instead of printing "0 m²", and it must
            // never substitute a placeholder figure.
            var r = JsonConvert.DeserializeObject<SuggestResult>(
                "{\"success\":true,\"soa\":{\"items\":[],\"total_gfa_m2\":0},\"schemes\":[]}");

            Assert.Null(r.SiteAreaM2);
            Assert.Null(r.SetbackM);
            Assert.Null(r.TargetGfaM2);
            Assert.Null(r.BuildingType);
            Assert.Null(r.BuildingTypeLabel);
        }

        [Fact]
        public void LivePayload_EverySoaRowSaysWhichStoreyItOccupies()
        {
            // Before `levels` landed, 4 of 6 rows had level=null and the screen
            // simply omitted the storey. Every row must now be able to say.
            foreach (var item in Live().Soa.Items)
            {
                Assert.False(string.IsNullOrEmpty(item.LevelLabel), $"{item.Key} has no storey");
                Assert.NotEmpty(item.Levels);
            }

            var soa = Live().Soa;
            Assert.Equal("Tingkat 1–2", soa.Items.Single(i => i.Key == "bilik_darjah").LevelLabel);
            Assert.Equal("Tingkat 1", soa.Items.Single(i => i.Key == "kantin").LevelLabel);
        }

        [Theory]
        // Contiguous runs collapse to a dash; gaps stay comma-separated so a
        // 4-storey school never prints "Tingkat 1, 2, 3, 4".
        [InlineData(new[] { 1 }, "Tingkat 1")]
        [InlineData(new[] { 1, 2 }, "Tingkat 1–2")]
        [InlineData(new[] { 1, 2, 3, 4 }, "Tingkat 1–4")]
        [InlineData(new[] { 1, 3 }, "Tingkat 1, 3")]
        [InlineData(new[] { 2, 1 }, "Tingkat 1–2")]          // unsorted input
        public void LevelLabel_CollapsesRuns(int[] levels, string expected)
        {
            Assert.Equal(expected, new SoaItem { Levels = levels.ToList() }.LevelLabel);
        }

        [Fact]
        public void LevelLabel_FallsBackToSingleLevelThenNull()
        {
            Assert.Equal("Tingkat 3", new SoaItem { Level = 3 }.LevelLabel);
            Assert.Null(new SoaItem().LevelLabel);
            Assert.Null(new SoaItem { Levels = new List<int>() }.LevelLabel);
        }

        [Fact]
        public void LivePayload_OverProvisionedSchemesCarryAWarning()
        {
            // The backend now flags schemes >25% over target. The tightest scheme
            // legitimately has none, so this asserts "some but not all" rather than
            // pinning a count that will move as the generator changes.
            var schemes = Live().Schemes;

            Assert.Contains(schemes, s => s.Warnings != null && s.Warnings.Count > 0);
            Assert.Contains(schemes, s => s.Warnings == null || s.Warnings.Count == 0);

            // Every returned scheme meets the GFA target BY DESIGN — failures are
            // moved to rejected[] and never appear here. So the pane must render the
            // "fails" state from rejected[], not from a failing scheme card.
            Assert.All(schemes, s => Assert.True(s.MeetsGfa));
            Assert.All(schemes, s => Assert.True(s.MarginM2 >= 0));
            Assert.NotEmpty(Live().Rejected);
        }

        [Fact]
        public void LivePayload_RoomTypesAllHaveARealPaletteEntry()
        {
            // A type the palette doesn't know still draws (hash fallback), but it
            // draws in someone else's colour and gets the wrong legend label. If the
            // live backend emits a type we haven't shipped, that should be a build
            // failure here, not a puzzling screenshot.
            var types = Live().Schemes
                .SelectMany(s => s.Rooms)
                .Select(r => r.Type)
                .Distinct()
                .ToList();

            Assert.NotEmpty(types);
            foreach (var t in types)
                Assert.Equal(t, MassingPalette.For(t).Type);
        }

        [Fact]
        public void LivePayload_BuildArgs_AreExactMillimetres()
        {
            foreach (var scheme in Live().Schemes)
            {
                var args = MassingArgs.Build(scheme);
                var rooms = (List<object>)args["rooms"];

                // The padang is site area — never a slab.
                var buildable = scheme.Rooms.Where(r => r.CountsAsGfa).ToList();
                Assert.Contains(scheme.Rooms, r => !r.CountsAsGfa);
                Assert.Equal(buildable.Count, rooms.Count);

                for (int i = 0; i < buildable.Count; i++)
                {
                    var src = buildable[i];
                    var dst = (Dictionary<string, object>)rooms[i];
                    var loop = (List<object>)dst["boundary_mm"];

                    Assert.Equal(4, loop.Count);
                    Assert.Equal(src.Level, dst["level"]);

                    var p0 = (List<object>)loop[0];
                    var p2 = (List<object>)loop[2];

                    // Exactly one ×1000: a 7.2 m bay is 7200 mm, not 7.2 or 7 200 000.
                    Assert.Equal(src.X * 1000.0, (double)p0[0], 6);
                    Assert.Equal(src.Y * 1000.0, (double)p0[1], 6);
                    Assert.Equal((src.X + src.W) * 1000.0, (double)p2[0], 6);
                    Assert.Equal((src.Y + src.H) * 1000.0, (double)p2[1], 6);

                    // Sanity band: a real room is between 1 m and 200 m per side.
                    double w = (double)p2[0] - (double)p0[0];
                    double h = (double)p2[1] - (double)p0[1];
                    Assert.InRange(w, 1000.0, 200000.0);
                    Assert.InRange(h, 1000.0, 200000.0);
                }

                // One level spec per storey, carrying its own index, 4 m floor-to-floor.
                var levels = (List<object>)args["levels"];
                Assert.Equal(2, levels.Count);
                Assert.Equal("Tingkat 1", (string)((Dictionary<string, object>)levels[0])["name"]);
                Assert.Equal(0.0, (double)((Dictionary<string, object>)levels[0])["elevation_mm"]);
                Assert.Equal(2, ((Dictionary<string, object>)levels[1])["level"]);
                Assert.Equal(4000.0, (double)((Dictionary<string, object>)levels[1])["elevation_mm"]);

                Assert.False((bool)args["make_walls"]);
                Assert.StartsWith("Massing — ", (string)args["option_name"]);
            }
        }

        [Fact]
        public void LivePayload_EveryRoomFitsInsideThePreviewSurface()
        {
            const double W = 380, H = 300;   // the canvas size PlanningView declares

            foreach (var scheme in Live().Schemes)
            foreach (var level in scheme.Levels())
            {
                var rooms = scheme.Rooms.Where(r => r.Level == level).ToList();
                var fit = PlanFit.Fit(rooms, W, H);
                Assert.False(fit.IsEmpty);

                foreach (var r in rooms)
                {
                    fit.RectOf(r, out var left, out var top, out var w, out var h);
                    Assert.InRange(left, -0.5, W + 0.5);
                    Assert.InRange(top, -0.5, H + 0.5);
                    Assert.InRange(left + w, -0.5, W + 0.5);
                    Assert.InRange(top + h, -0.5, H + 0.5);
                }
            }
        }
    }
}
