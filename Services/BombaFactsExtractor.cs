using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Measured model facts for the Bomba band resolution. Only facts the
    /// model actually carries: a value we cannot measure is null, and the
    /// backend then ASKS (needs_input) instead of guessing — never a default.
    /// </summary>
    public class BombaModelFacts
    {
        public string ProjectName { get; set; }
        public string FileName { get; set; }
        public double? FloorAreaM2 { get; set; }
        public double? HeightMm { get; set; }
        public int RoomCount { get; set; }
        /// Count of levels that own placed rooms — the storey count the
        /// schedule's "tingkat" bands key on. Null when no rooms are placed.
        public int? Storeys { get; set; }
        public List<string> SearchedModels { get; set; }

        public BombaModelFacts() { SearchedModels = new List<string>(); }

        /// One mono line for the setup card — what the model measured, so the
        /// drafter sees why a band resolved (or why the pane must ask).
        public string Label
        {
            get
            {
                var area = FloorAreaM2.HasValue
                    ? FloorAreaM2.Value.ToString("0.#") + " m² largest storey"
                    : "no placed rooms — area unknown";
                var height = HeightMm.HasValue
                    ? (HeightMm.Value / 1000.0).ToString("0.#") + " m height"
                    : "height unknown";
                return "measured · " + area + " · " + height;
            }
        }
    }

    public static class BombaFactsExtractor
    {
        private const double SqFtToSqM = 0.09290304;
        private const double FtToMm = 304.8;

        public static BombaModelFacts Extract(Document doc)
        {
            var facts = new BombaModelFacts();
            facts.ProjectName = doc.ProjectInformation != null ? (doc.ProjectInformation.Name ?? "") : "";
            facts.FileName = doc.Title ?? "";

            // Phase 1 searches the HOST model only. Fire systems live in the
            // M&E discipline, and "M&E" is deliberately absent from this list
            // until link-reading lands: the backend then answers NOT CHECKED
            // ("link M&E and re-check") rather than a false "missing".
            facts.SearchedModels.Add("Architecture");

            // Floor area PER STOREY — the quantity the schedule bands key on
            // ("total floor area per storey"), so the largest storey governs.
            // A whole-model sum resolved the wrong band on any multi-storey
            // building. Unplaced/unenclosed rooms report Area == 0 and are
            // excluded; a model with no placed rooms yields null (cannot
            // measure), not 0 (measured empty).
            var perLevelSqFt = new Dictionary<long, double>();
            int roomCount = 0;
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>();
            foreach (var room in rooms)
            {
                if (room.Area <= 0) continue;
                roomCount++;
#if REVIT2023_24
                long levelKey = room.LevelId != null ? room.LevelId.IntegerValue : -1;
#else
                long levelKey = room.LevelId != null ? room.LevelId.Value : -1;
#endif
                double sum;
                perLevelSqFt.TryGetValue(levelKey, out sum);
                perLevelSqFt[levelKey] = sum + room.Area;
            }
            facts.RoomCount = roomCount;
            if (roomCount > 0)
            {
                facts.FloorAreaM2 = Math.Round(perLevelSqFt.Values.Max() * SqFtToSqM, 1);
                // Storeys = levels owning placed rooms. A roof or plant level
                // with no rooms is not a storey the schedule counts.
                facts.Storeys = perLevelSqFt.Count;
            }

            // Building height: top level minus bottom level. Two levels
            // minimum — a single-level model cannot state a height.
            var elevations = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Select(l => l.Elevation)
                .ToList();
            if (elevations.Count >= 2)
                facts.HeightMm = Math.Round((elevations.Max() - elevations.Min()) * FtToMm, 0);

            return facts;
        }

        // ── purpose-group detection (design 10A: "auto-detected from room names") ──

        /// What the room names point to. A read, not an assertion: the pane
        /// shows the evidence and tags the value "auto"; the drafter's pick
        /// always wins. No rooms, or no bucket clearing the bar → null Bucket
        /// (unknown is unknown — never a default).
        public class PurposeGuess
        {
            public string Bucket;        // "office" | "assembly" | "shop" | "hospital" | "school" | "hotel" | "residential"
            public int Matched;          // rooms that voted for the winning bucket
            public int Total;            // placed, named rooms read
            public string Evidence;      // one line for the pane, e.g. "14 of 24 room names match (pejabat, bilik mesyuarat …)"
        }

        /// Lexicon of Malay/English room-name stems per bucket. Data, not
        /// prompt text; grow it here (word fragments, lower-case).
        private static readonly Dictionary<string, string[]> PgLexicon = new Dictionary<string, string[]>
        {
            { "office",      new[] { "pejabat", "office", "bilik mesyuarat", "meeting", "workstation" } },
            { "assembly",    new[] { "surau", "dewan", "hall", "auditorium", "masjid", "ruang legar", "anjung", "pentas", "bilik persalinan", "bilik darjah", "class", "makmal", "lab" } },
            { "shop",        new[] { "kedai", "shop", "retail", "kiosk", "gerai" } },
            { "hospital",    new[] { "wad", "ward", "klinik", "clinic", "rawatan", "treatment", "farmasi" } },
            { "school",      new[] { "bilik darjah", "classroom", "kelas", "sekolah" } },
            { "hotel",       new[] { "bilik tidur", "guest room", "suite", "lobi hotel" } },
            { "residential", new[] { "rumah", "unit", "apartment", "bilik tidur utama" } },
        };

        public static PurposeGuess DetectPurposeGroup(Document doc)
        {
            var votes = new Dictionary<string, int>();
            var samples = new Dictionary<string, List<string>>();
            int total = 0;
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>();
            foreach (var room in rooms)
            {
                if (room.Area <= 0) continue;
                var name = (room.Name ?? "").ToLowerInvariant();
                if (name.Length == 0) continue;
                total++;
                foreach (var pair in PgLexicon)
                {
                    bool hit = false;
                    foreach (var stem in pair.Value)
                        if (name.Contains(stem)) { hit = true; break; }
                    if (!hit) continue;
                    int v; votes.TryGetValue(pair.Key, out v);
                    votes[pair.Key] = v + 1;
                    List<string> s;
                    if (!samples.TryGetValue(pair.Key, out s)) samples[pair.Key] = s = new List<string>();
                    if (s.Count < 3 && !s.Contains(room.Name)) s.Add(room.Name);
                    break; // first bucket wins per room — buckets are ordered most→least specific enough for a suggestion
                }
            }
            if (total == 0 || votes.Count == 0) return new PurposeGuess { Total = total };

            string best = null; int bestN = 0;
            foreach (var pair in votes)
                if (pair.Value > bestN) { best = pair.Key; bestN = pair.Value; }

            // The bar: at least 3 voting rooms and a majority of the votes cast.
            int cast = votes.Values.Sum();
            if (bestN < 3 || bestN * 2 < cast) return new PurposeGuess { Total = total };

            return new PurposeGuess
            {
                Bucket = best,
                Matched = bestN,
                Total = total,
                Evidence = bestN + " of " + total + " room names match ("
                    + string.Join(", ", samples[best].ToArray()) + " …)",
            };
        }

        // ── M&E link visibility (design 10A: the M&E scope row) ──────────────

        public class MneLinkInfo
        {
            public string Name;
            public bool Loaded;
        }

        /// Linked models whose name reads as M&E/MEP/fire. Phase 1 never
        /// searches them — this only feeds the Home row so the drafter can see
        /// why fire systems answer NOT CHECKED, and whether a link is unloaded.
        public static List<MneLinkInfo> ListMneLinks(Document doc)
        {
            var result = new List<MneLinkInfo>();
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>();
            foreach (var link in links)
            {
                var name = link.Name ?? "";
                var low = name.ToLowerInvariant();
                if (!(low.Contains("mep") || low.Contains("m&e") || low.Contains("mne")
                      || low.Contains("mech") || low.Contains("elect") || low.Contains("fire")
                      || low.Contains("bomba")
                      // JKR discipline file prefixes: jkrME24_…, jkrEL24_…
                      || low.StartsWith("jkrme") || low.StartsWith("jkrel")))
                    continue;
                // Link display names carry " : location" suffixes — trim to the file part.
                var cut = name.IndexOf(':');
                result.Add(new MneLinkInfo
                {
                    Name = (cut > 0 ? name.Substring(0, cut) : name).Trim(),
                    Loaded = link.GetLinkDocument() != null,
                });
            }
            return result;
        }
    }
}
