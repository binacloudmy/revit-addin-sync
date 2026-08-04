using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.SpacePlanning.Model;

namespace UiHarness
{
    /// <summary>
    /// A representative /planning/suggest response for the harness preview — the
    /// frozen sample payload from the spec, widened into a full 18-classroom school
    /// so the floor-plan canvas, the legend and the L1/L2 toggle all have something
    /// real to draw. Areas are SUMMED FROM THE ROOMS rather than hardcoded, so the
    /// figures on screen always agree with the geometry being previewed.
    /// </summary>
    internal static class MassingSample
    {
        private const double Bay = 7.2;      // JKR classroom bay
        private const double BayDepth = 9.0;
        private const double TargetGfa = 2372.4;

        internal static SuggestResult School() => new SuggestResult
        {
            Success = true,
            // PROGRAM READ chips. site/setback are only present when the brief states
            // them or the caller sends them, so the harness sets them to exercise the
            // populated case; the empty case is covered by a unit test.
            SiteAreaM2 = 5800,
            SetbackM = 6,
            TargetGfaM2 = TargetGfa,
            BuildingType = "sekolah_rendah",
            Soa = Soa(),
            Schemes = new List<MassingScheme>
            {
                Scheme("A", "Dua Blok Selari", originX: 6, stagger: 0, upperKelas: 9),
                Scheme("B", "Blok Berperingkat", originX: 6, stagger: 3.6, upperKelas: 8),
                // Fewer upper-storey classrooms, so the three cards carry genuinely
                // different figures (identical numbers read as a bug in review).
                Scheme("C", "Blok Berkelompok", originX: 12, stagger: 7.2, upperKelas: 7),
            },
            Rejected = new List<RejectedScheme>
            {
                new RejectedScheme { Id = "A1", Title = "Dua Blok Selari — 1 tingkat", TotalGfaM2 = 1731.6, GapM2 = 640.8, Reason = "below_target_gfa" },
                new RejectedScheme { Id = "B1", Title = "Blok Berperingkat — tapak sempit", TotalGfaM2 = 1544.4, GapM2 = 828.0, Reason = "below_target_gfa" },
                new RejectedScheme { Id = "C1", Title = "Blok Berkelompok — 1 tingkat", TotalGfaM2 = 1620.0, GapM2 = 752.4, Reason = "below_target_gfa" },
            },
            Stats = new MassingStats
            {
                TargetGfaM2 = TargetGfa, SchemeCount = 6, PassingCount = 3,
                RejectedCount = 3, BestMarginM2 = 1158.5,
            },
        };

        private static Soa Soa() => new Soa
        {
            TotalGfaM2 = TargetGfa,
            Notes = "Sanitary counts are advisory — verify against the Authority Having Jurisdiction.",
            Items = new List<SoaItem>
            {
                new SoaItem { Key = "bilik_darjah", LabelMs = "Bilik Darjah", Count = 18, UnitAreaM2 = 64.8, TotalAreaM2 = 1166.4, Levels = L(1, 2), Source = "JKR Sekolah Modul", Clause = "bay 7.2 x 9.0 m", Advisory = true },
                new SoaItem { Key = "bilik_sokongan", LabelMs = "Bilik Sokongan", Count = 8, UnitAreaM2 = 48.6, TotalAreaM2 = 388.8, Source = "JKR/KPM", Levels = L(1, 2), Clause = "support bay 5.4 x 9.0 m", Advisory = true },
                new SoaItem { Key = "tandas", LabelMs = "Blok Tandas", Count = 4, UnitAreaM2 = 64.8, TotalAreaM2 = 259.2, Levels = L(1, 2), Source = "JKR Sekolah Modul", Clause = "toilet block bay", Advisory = true },
                new SoaItem { Key = "perhimpunan", LabelMs = "Dewan Perhimpunan", Count = 1, UnitAreaM2 = 414.0, TotalAreaM2 = 414.0, Level = 1, Levels = L(1), Source = "JKR/KPM", Clause = "min. assembly area", Advisory = true },
                new SoaItem { Key = "kantin", LabelMs = "Kantin", Count = 1, UnitAreaM2 = 144.0, TotalAreaM2 = 144.0, Level = 1, Levels = L(1), Source = "JKR/KPM", Clause = "12 x 12 m", Advisory = true },
                new SoaItem { Key = "padang", LabelMs = "Padang", Count = 1, UnitAreaM2 = 900.0, TotalAreaM2 = 0.0, Level = 1, Levels = L(1), Source = "JKR/KPM", Clause = "min. 30 x 30 m (site, no GFA)", Advisory = true },
            },
            Sanitary = new List<FixtureReq>
            {
                new FixtureReq { Fixture = "wc", Gender = "male", Count = 7, Source = "UBBL 1984", Clause = "Eighth Schedule (By-law 47), educational" },
                new FixtureReq { Fixture = "wc", Gender = "female", Count = 11, Source = "UBBL 1984", Clause = "Eighth Schedule (By-law 47), educational" },
                new FixtureReq { Fixture = "urinal", Gender = "male", Count = 11, Source = "UBBL 1984", Clause = "Eighth Schedule (By-law 47), educational" },
                new FixtureReq { Fixture = "wash_basin", Gender = "all", Count = 14, Source = "UBBL 1984", Clause = "Eighth Schedule (By-law 47), educational" },
            },
        };

        /// <summary>Two parallel blocks with a covered walkway, 9 classrooms per
        /// level. <paramref name="stagger"/> shifts the upper storey so the three
        /// schemes read as genuinely different plans.</summary>
        private static MassingScheme Scheme(
            string id, string title, double originX, double stagger, int upperKelas)
        {
            var rooms = new List<MassingRoom>();

            // ── Tingkat 1: classroom row + walkway + shared facilities ──
            for (int i = 0; i < 9; i++)
                rooms.Add(Room($"1{(char)('A' + i)}", "kelas", originX + i * Bay, 6, Bay, BayDepth, 1));
            rooms.Add(Room("Selasar", "selasar", originX, 6 + BayDepth, 9 * Bay, 2.4, 1));

            double y2 = 6 + BayDepth + 2.4;
            rooms.Add(Room("Dewan Perhimpunan", "perhimpunan", originX, y2, 27.0, 9.0, 1));
            rooms.Add(Room("Kantin", "kantin", originX + 28.8, y2, 12.0, 12.0, 1));
            rooms.Add(Room("Tandas A", "tandas", originX + 42.0, y2, Bay, BayDepth, 1));
            rooms.Add(Room("Tandas B", "tandas", originX + 49.8, y2, Bay, BayDepth, 1));
            rooms.Add(Room("Pejabat", "sokongan", originX + 57.6, y2, Bay, 6.75, 1));

            // Site only — drawn dashed, never built as a slab.
            rooms.Add(new MassingRoom
            {
                Label = "Padang", Type = "padang",
                X = originX + 9 * Bay + 6, Y = 6, W = 30, H = 30, Level = 1,
                CountsAsGfa = false,
            });

            // ── Tingkat 2: classroom row + walkway + support rooms ──
            for (int i = 0; i < upperKelas; i++)
                rooms.Add(Room($"2{(char)('A' + i)}", "kelas", originX + stagger + i * Bay, 6, Bay, BayDepth, 2));
            rooms.Add(Room("Selasar", "selasar", originX + stagger, 6 + BayDepth, upperKelas * Bay, 2.4, 2));

            string[] support = { "Bilik Guru", "Bimbingan", "Keselamatan", "Bilik Sukan", "Koku", "Stor" };
            for (int i = 0; i < support.Length; i++)
                rooms.Add(Room(support[i], "sokongan", originX + stagger + i * Bay, y2, Bay, 6.75, 2));
            rooms.Add(Room("Tandas C", "tandas", originX + stagger + 6 * Bay, y2, Bay, BayDepth, 2));
            rooms.Add(Room("Tandas D", "tandas", originX + stagger + 7 * Bay, y2, Bay, BayDepth, 2));

            double l1 = Area(rooms, 1), l2 = Area(rooms, 2);
            double gfa = l1 + l2;

            return new MassingScheme
            {
                Id = id, Title = title, Rooms = rooms,
                LevelAreasM2 = new Dictionary<string, double> { ["1"] = l1, ["2"] = l2 },
                TotalGfaM2 = gfa,
                FootprintM2 = l1,
                TargetGfaM2 = TargetGfa,
                MarginM2 = gfa - TargetGfa,
                MeetsGfa = gfa >= TargetGfa,
                Warnings = id == "C"
                    ? new List<string> { "Padang overlaps the site boundary by ~2 m — verify setback." }
                    : new List<string>(),
            };
        }

        /// <summary>
        /// The zero-scheme case, from a REAL backend response (captured 2026-07-31,
        /// brief "sekolah rendah, Tahun 1-6 with 14 kelas each" — SK Cyberjaya's
        /// scale, 84 classrooms). The generator only produces two-storey layouts and
        /// tops out around 3,530 m², so every candidate is rejected and schemes[] is
        /// EMPTY. That is a state the screen must handle honestly, and nothing in
        /// the normal sample exercises it.
        /// </summary>
        internal static SuggestResult Oversized() => new SuggestResult
        {
            Success = true,
            SiteAreaM2 = 20000,
            SetbackM = 10,
            TargetGfaM2 = 6649.2,
            BuildingType = "sekolah_rendah",
            Soa = new Soa
            {
                TotalGfaM2 = 6649.2,
                Notes = "Counts and areas are auto-derived from Malaysian standards "
                      + "(UBBL 1984, JKR/KPM) and are ADVISORY — verify against the "
                      + "Authority Having Jurisdiction.",
                Items = new List<SoaItem>
                {
                    new SoaItem { Key = "bilik_darjah", LabelMs = "Bilik Darjah", Count = 84, UnitAreaM2 = 64.8, TotalAreaM2 = 5443.2, Levels = L(1, 2), Source = "JKR Sekolah Modul", Clause = "bay 7.2 x 9.0 m", Advisory = true },
                    new SoaItem { Key = "bilik_sokongan", LabelMs = "Bilik Sokongan", Count = 8, UnitAreaM2 = 48.6, TotalAreaM2 = 388.8, Levels = L(1, 2), Source = "JKR Sekolah Modul", Clause = "support bay 5.4 x 9.0 m", Advisory = true },
                    new SoaItem { Key = "tandas", LabelMs = "Blok Tandas", Count = 4, UnitAreaM2 = 64.8, TotalAreaM2 = 259.2, Levels = L(1, 2), Source = "JKR Sekolah Modul", Clause = "toilet block bay", Advisory = true },
                    new SoaItem { Key = "perhimpunan", LabelMs = "Dewan Perhimpunan", Count = 1, UnitAreaM2 = 414.0, TotalAreaM2 = 414.0, Level = 1, Levels = L(1), Source = "JKR/KPM", Clause = "assembly hall min. area", Advisory = true },
                    new SoaItem { Key = "kantin", LabelMs = "Kantin", Count = 1, UnitAreaM2 = 144.0, TotalAreaM2 = 144.0, Level = 1, Levels = L(1), Source = "JKR/KPM", Clause = "canteen 12 x 12 m", Advisory = true },
                    new SoaItem { Key = "padang", LabelMs = "Padang", Count = 1, UnitAreaM2 = 900.0, TotalAreaM2 = 0.0, Level = 1, Levels = L(1), Source = "JKR/KPM", Clause = "field min. 30 x 30 m (site, no GFA)", Advisory = true },
                },
                Sanitary = new List<FixtureReq>
                {
                    new FixtureReq { Fixture = "wc", Gender = "male", Count = 32, Source = "UBBL 1984", Clause = "Eighth Schedule (By-law 47), educational" },
                    new FixtureReq { Fixture = "wc", Gender = "female", Count = 51, Source = "UBBL 1984", Clause = "Eighth Schedule (By-law 47), educational" },
                    new FixtureReq { Fixture = "urinal", Gender = "male", Count = 51, Source = "UBBL 1984", Clause = "Eighth Schedule (By-law 47), educational" },
                    new FixtureReq { Fixture = "wash_basin", Gender = "all", Count = 63, Source = "UBBL 1984", Clause = "Eighth Schedule (By-law 47), educational" },
                },
            },
            Schemes = new List<MassingScheme>(),          // ← the point of this fixture
            Rejected = new List<RejectedScheme>
            {
                new RejectedScheme { Id = "A",  Title = "Dua Blok Selari",             TotalGfaM2 = 2962.8, GapM2 = 3686.4, Reason = "below_target_gfa" },
                new RejectedScheme { Id = "A1", Title = "Dua Blok Selari - 1 tingkat", TotalGfaM2 = 1731.6, GapM2 = 4917.6, Reason = "below_target_gfa" },
                new RejectedScheme { Id = "B",  Title = "Sisir",                       TotalGfaM2 = 3262.3, GapM2 = 3386.9, Reason = "below_target_gfa" },
                new RejectedScheme { Id = "B1", Title = "Sisir - 1 tingkat",           TotalGfaM2 = 1910.4, GapM2 = 4738.8, Reason = "below_target_gfa" },
                new RejectedScheme { Id = "C",  Title = "Courtyard",                   TotalGfaM2 = 3530.9, GapM2 = 3118.3, Reason = "below_target_gfa" },
                new RejectedScheme { Id = "C1", Title = "Courtyard - 1 tingkat",       TotalGfaM2 = 2053.4, GapM2 = 4595.8, Reason = "below_target_gfa" },
            },
            Stats = new MassingStats
            {
                TargetGfaM2 = 6649.2, SchemeCount = 6, PassingCount = 0, RejectedCount = 6,
            },
        };

        private static List<int> L(params int[] levels) => new List<int>(levels);

        private static double Area(IEnumerable<MassingRoom> rooms, int level) =>
            rooms.Where(r => r.Level == level && r.CountsAsGfa).Sum(r => r.W * r.H);

        private static MassingRoom Room(
            string label, string type, double x, double y, double w, double h, int level) =>
            new MassingRoom { Label = label, Type = type, X = x, Y = y, W = w, H = h, Level = level };
    }
}
