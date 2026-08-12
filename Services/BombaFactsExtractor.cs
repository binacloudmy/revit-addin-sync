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
                facts.FloorAreaM2 = Math.Round(perLevelSqFt.Values.Max() * SqFtToSqM, 1);

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
    }
}
