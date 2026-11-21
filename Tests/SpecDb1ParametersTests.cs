using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Db.1 driven by the Buku Parameter AR tables (Services/JkrSpec/data/jkr_pb_ar_parameters.csv, Linear AI-503):
    /// the data itself, the data fixes, and the rule at LOD 200 / 300 / 400.
    /// </summary>
    public class SpecDb1ParametersTests
    {
        private static long _id = 5000;

        private static SpecType Type(string revitCategory, string name = "(X001a) 100")
        {
            var t = new SpecType { TypeId = ++_id, Category = revitCategory, TypeName = name, InstanceCount = 1, InstanceIds = { ++_id }, ParamNamesRead = true };
            return t;
        }

        /// <summary>Every parameter the book requires (not starred) at <paramref name="loi"/> filled, or carried when not text.</summary>
        private static SpecType Complete(string revitCategory, int loi)
        {
            var t = Type(revitCategory);
            var book = SpecPbParameters.BookCategoryOf(revitCategory);
            foreach (var p in SpecPbParameters.At(book, loi).Where(x => x.IsShared))
            {
                if (p.IsTextValued) t.Texts[p.Name] = "x"; else t.ParamNames.Add(p.Name);
            }
            return t;
        }

        private static List<SpecResult> Run(int lod, params SpecType[] types)
        {
            var m = new SpecModel { Lod = lod };
            m.Types.AddRange(types);
            return SpecChecks.Rules["Db.1"](m.Normalize()).ToList();
        }

        private static List<SpecResult> Fails(List<SpecResult> r) => r.Where(x => x.Status == "fail").ToList();

        // ── the data ──

        [Fact]
        public void The_table_holds_all_20_AR_categories_and_984_rows()
        {
            var all = SpecPbParameters.All;
            Assert.Equal(984, all.Count);   // the spike's 985 minus the duplicate Stair Komponen_IBS_jkr_sty
            Assert.Equal(20, all.Select(p => p.Category).Distinct().Count());
            Assert.All(SpecPbData.ArCategories, c => Assert.NotEmpty(SpecPbParameters.ForBookCategory(c.Name)));
            Assert.Equal(76, all.Count(p => p.Starred));
            Assert.Equal(3, all.Count(p => p.Star == 2));
            Assert.Equal(86, all.Count(p => !p.IsShared));   // the plain Revit built-ins (Name, Base Level, Description …), not judged
        }

        [Fact]
        public void The_counts_match_the_spike_per_category_and_LOi()
        {
            // Spike AI-488 table: "plain (+starred)" ticked at LOi 200 / 300 / 400.
            void Check(string cat, int loi, int plain, int starred)
            {
                var rows = SpecPbParameters.At(cat, loi);
                Assert.Equal(plain, rows.Count(p => !p.Starred));
                Assert.Equal(starred, rows.Count(p => p.Starred));
            }
            Check("Wall", 200, 15, 1); Check("Wall", 300, 27, 1); Check("Wall", 400, 46, 2);
            Check("Door", 200, 4, 3); Check("Door", 300, 20, 7); Check("Door", 400, 49, 11);
        }

        [Fact]
        public void The_data_fixes_are_in_the_table()
        {
            var all = SpecPbParameters.All;
            Assert.DoesNotContain(all, p => p.Name.Contains('.') || p.Name.Contains(' ') && p.IsShared);
            Assert.Equal(7, all.Count(p => p.Name == "Kadaran_jkr_sit"));                         // 'Kadaran_jkr.sit' (dot typo on 5 pages) normalised, 7 categories carry it
            Assert.Contains(all, p => p.Name == "Catatan_Attribut_jkr_sit");                       // stray spaces inside a wrapped name removed
            Assert.Equal("Type", all.Single(p => p.Category == "Room" && p.Name == "Kod_Ruang_AR_jkr_stt").Kind);      // suffix over heading
            Assert.Equal("Instance", all.Single(p => p.Category == "Window" && p.Name == "Warna_Kelulusan_PBT_jkr_sit").Kind);
            var ibs = all.Single(p => p.Category == "Stair" && p.Name == "Komponen_IBS_jkr_sty");                      // deduped
            Assert.Equal((300, 500), (ibs.FromLoi, ibs.ToLoi));                                                        // the narrower of 100–500 / 300–500
            Assert.Equal(all.Count, all.Select(p => (p.Category, p.Name, p.Kind)).Distinct().Count());
            // The suffix and the Type/Instance column agree on every shared row.
            Assert.All(all.Where(p => p.IsShared), p => Assert.Equal(p.Suffix[p.Suffix.Length - 2] == 't' ? "Type" : "Instance", p.Kind));
        }

        [Fact]
        public void The_common_set_at_LOi_400_is_14_including_the_two_the_old_list_missed()
        {
            var cats = SpecPbData.ArCategories.Where(c => c.Name != "Area" && c.Name != "Room").Select(c => c.Name).ToList();
            Assert.Equal(18, cats.Count);
            var common = SpecPbParameters.All
                .Where(p => p.IsShared && !p.Starred && cats.Contains(p.Category) && p.FromLoi <= 400 && p.ToLoi == 500)
                .GroupBy(p => p.Name).Where(g => g.Select(x => x.Category).Distinct().Count() == 18)
                .Select(g => g.Key).ToList();
            Assert.Equal(14, common.Count);
            Assert.Contains("No_Pesanan_Rasmi_Kerajaan_jkr_sit", common);
            Assert.Contains("Kos_Perolehan_jkr_sic", common);
            Assert.Contains("Kod_Jenis_jkr_stt", common);
        }

        [Fact]
        public void Kod_Jenis_starts_at_LOi_300_in_13_of_18_categories_and_400_in_the_other_5()
        {
            var k = SpecPbParameters.All.Where(p => p.Name == "Kod_Jenis_jkr_stt").ToList();
            Assert.Equal(13, k.Count(p => p.FromLoi == 300));
            Assert.Equal(5, k.Count(p => p.FromLoi == 400));
        }

        [Theory]
        [InlineData("Doors", "Door")]
        [InlineData("Structural Columns", "Column")]
        [InlineData("Columns", "Column")]
        [InlineData("Curtain Panels", "Curtain System")]
        [InlineData("Walls", "Wall")]
        [InlineData("Rooms", "Room")]
        public void A_Revit_category_maps_to_its_book_category(string revit, string book) =>
            Assert.Equal(book, SpecPbParameters.BookCategoryOf(revit));

        // ── the rule: Wall and Door at LOD 200 / 300 / 400 ──

        [Theory]
        [InlineData("Walls", 200)]
        [InlineData("Walls", 300)]
        [InlineData("Walls", 400)]
        [InlineData("Doors", 200)]
        [InlineData("Doors", 300)]
        [InlineData("Doors", 400)]
        public void A_type_with_every_required_parameter_filled_passes_at_its_LOD(string category, int lod)
        {
            var r = Run(lod, Complete(category, lod));
            Assert.Empty(Fails(r));
            var pass = Assert.Single(r, x => x.Status == "pass" && x.Coverage == SpecCoverage.FailOnly);
            Assert.Contains("LOi " + lod, pass.Note);
            Assert.Contains("LOi " + lod, pass.NoteEn);
        }

        [Theory]
        [InlineData("Walls", 200)]
        [InlineData("Walls", 300)]
        [InlineData("Walls", 400)]
        [InlineData("Doors", 200)]
        [InlineData("Doors", 300)]
        [InlineData("Doors", 400)]
        public void Leaving_a_required_text_parameter_empty_fails_and_says_the_LOi(string category, int lod)
        {
            var t = Complete(category, lod);
            var book = SpecPbParameters.BookCategoryOf(category);
            var victim = SpecPbParameters.At(book, lod).First(p => p.IsShared && !p.Starred && p.IsTextValued);
            t.Texts[victim.Name] = " ";
            var f = Assert.Single(Fails(Run(lod, t)));
            Assert.Contains("kosong: " + victim.Name, f.Note);
            Assert.Contains($"wajib LOi {lod}", f.Note);
            Assert.Contains($"mandatory LOi {lod}", f.NoteEn);
            Assert.Contains("PB hlm. ", f.Note);
            Assert.Equal(SpecCoverage.FailOnly, f.Coverage);
        }

        [Fact]
        public void A_parameter_the_book_ticks_only_from_a_later_LOi_is_not_failed_earlier()
        {
            // First Wall text parameter that starts at LOi 400: empty at LOD 300 passes, empty at LOD 400 fails.
            var late = SpecPbParameters.ForBookCategory("Wall").First(p => p.IsShared && !p.Starred && p.IsTextValued && p.FromLoi == 400);
            var at300 = Complete("Walls", 300); at300.Texts[late.Name] = "";
            Assert.Empty(Fails(Run(300, at300)));
            var at400 = Complete("Walls", 400); at400.Texts[late.Name] = "";
            Assert.Contains(late.Name, Assert.Single(Fails(Run(400, at400))).Note);
        }

        [Fact]
        public void LOD_350_is_judged_at_LOi_300_and_LOD_450_at_LOi_400()
        {
            var t = Complete("Walls", 300);
            Assert.Empty(Fails(Run(350, t)));
            var f = Assert.Single(Fails(Run(450, Complete("Walls", 400).Also(x => x.Texts["Kod_Jenis_jkr_stt"] = ""))));
            Assert.Contains("LOi 400", f.Note);
            Assert.DoesNotContain("LOi 450", f.Note);
        }

        // ── Kod_Jenis from LOi 300 (Door), the two added common parameters at 400 ──

        [Fact]
        public void A_Doors_empty_Kod_Jenis_fails_at_LOD_300_and_names_the_LOi_it_starts_at()
        {
            var t = Complete("Doors", 300); t.Texts["Kod_Jenis_jkr_stt"] = "";
            var f = Assert.Single(Fails(Run(300, t)));
            Assert.Contains("Kod_Jenis_jkr_stt", f.Note);
            Assert.Empty(Fails(Run(200, Complete("Doors", 200).Also(x => x.Texts["Kod_Jenis_jkr_stt"] = ""))));
            // At LOD 400 the same parameter is still named with the LOi it began at.
            var f4 = Assert.Single(Fails(Run(400, Complete("Doors", 400).Also(x => x.Texts["Kod_Jenis_jkr_stt"] = ""))));
            Assert.Contains("Kod_Jenis_jkr_stt (dari LOi 300)", f4.Note);
            Assert.Contains("Kod_Jenis_jkr_stt (from LOi 300)", f4.NoteEn);
        }

        [Fact]
        public void A_Floors_Kod_Jenis_is_not_required_at_LOD_300_it_starts_at_400()
        {
            var t = Complete("Floors", 300);
            t.Texts["Kod_Jenis_jkr_stt"] = "";
            Assert.Empty(Fails(Run(300, t)));
            Assert.Single(Fails(Run(400, Complete("Floors", 400).Also(x => x.Texts["Kod_Jenis_jkr_stt"] = ""))));
        }

        [Fact]
        public void The_two_added_common_parameters_are_judged_at_LOD_400()
        {
            // No_Pesanan_Rasmi_Kerajaan_jkr_sit: text, empty.
            var empty = Complete("Walls", 400); empty.Texts["No_Pesanan_Rasmi_Kerajaan_jkr_sit"] = "";
            Assert.Contains("kosong: No_Pesanan_Rasmi_Kerajaan_jkr_sit", Assert.Single(Fails(Run(400, empty))).Note);
            // Kos_Perolehan_jkr_sic: currency, so a value is not in the snapshot; absent from the type = missing.
            var missing = Complete("Walls", 400); missing.ParamNames.Remove("Kos_Perolehan_jkr_sic");
            Assert.Contains("tiada pada type: Kos_Perolehan_jkr_sic", Assert.Single(Fails(Run(400, missing))).Note);
            // Not required at 300 for a Wall.
            Assert.DoesNotContain(SpecPbParameters.At("Wall", 300), p => p.Name == "No_Pesanan_Rasmi_Kerajaan_jkr_sit" || p.Name == "Kos_Perolehan_jkr_sic");
        }

        [Fact]
        public void A_carried_parameter_that_is_not_text_is_not_judged_empty()
        {
            var t = Complete("Walls", 400);   // Kos_Perolehan_jkr_sic carried, no text to read
            Assert.Empty(Fails(Run(400, t)));
        }

        // ── starred: listed, never failed ──

        [Fact]
        public void A_starred_parameter_left_empty_is_listed_not_failed()
        {
            var star = SpecPbParameters.At("Door", 300).First(p => p.IsShared && p.Starred && p.IsTextValued);
            var t = Complete("Doors", 300);
            t.Texts[star.Name] = "";
            var r = Run(300, t);
            Assert.Empty(Fails(r));
            var listing = Assert.Single(r, x => x.Coverage == SpecCoverage.Listing);
            Assert.Contains(star.Name + "*", listing.Note);
            Assert.Contains("tidak digagalkan", listing.Note);
            Assert.Contains("not failed", listing.NoteEn);
            Assert.Equal("Doors", listing.Category);
        }

        [Fact]
        public void The_double_star_parameters_are_listed_too()
        {
            var t = Complete("Doors", 400);
            var r = Run(400, t);
            Assert.Contains("Kod_Ruang_Berkaitan_jkr_stt**", Assert.Single(r, x => x.Coverage == SpecCoverage.Listing).Note);
        }

        // ── Room / Area ──

        [Fact]
        public void A_room_with_a_blank_required_text_parameter_fails_per_room_group()
        {
            var p = SpecPbParameters.At("Room", 200).First(x => x.IsShared && !x.Starred && x.IsTextValued);
            var room = new SpecRoom { Id = 77, Name = "Bilik" };
            room.Params[p.Name] = "";
            var good = new SpecRoom { Id = 78, Name = "Bilik 2" };
            good.Params[p.Name] = "ok";
            var m = new SpecModel { Lod = 200 };
            m.Rooms.Add(room); m.Rooms.Add(good);
            var r = SpecChecks.Rules["Db.1"](m.Normalize()).ToList();
            var f = Assert.Single(Fails(r));
            Assert.Equal("Room", f.Category);
            Assert.Contains(p.Name, f.Note);
            Assert.Contains("LOi 200", f.Note);
            Assert.Equal(new long[] { 77 }, f.ElementIds);
            Assert.Single(r, x => x.Status == "pass" && x.Category == "Room" && x.ElementCount == 1);
        }

        [Fact]
        public void A_room_that_does_not_carry_the_parameters_is_not_counted_as_passing()
        {
            var m = new SpecModel { Lod = 200 };
            m.Rooms.Add(new SpecRoom { Id = 1, Name = "Bilik" });
            Assert.Empty(SpecChecks.Rules["Db.1"](m.Normalize()));
        }

        // ── scoring ──

        [Fact]
        public void The_check_stays_FailOnly_because_only_text_and_presence_are_judged()
        {
            var r = Run(300, Complete("Doors", 300));
            Assert.DoesNotContain(r, x => x.Coverage == SpecCoverage.Full);
        }
    }

    internal static class Db1TestExt
    {
        public static T Also<T>(this T x, System.Action<T> f) { f(x); return x; }
    }
}
