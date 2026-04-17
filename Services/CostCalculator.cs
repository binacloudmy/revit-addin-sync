using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Calculates cost summaries from a list of cost items.
    /// Groups by level and category with totals and percentages.
    /// </summary>
    public static class CostCalculator
    {
        /// <summary>
        /// Preset building type rates in RM per sqft for Malaysian construction.
        /// Sources: BCISM Costbook 2022 (CIDB/N3C), JUBM Construction Cost Handbook 2024,
        ///          Arcadis Construction Cost Handbook MY 2022, adjusted for 2024/2025 levels.
        /// Rates are inclusive of building + M&E + preliminaries (10%), exclusive of contingencies.
        /// </summary>
        public static readonly Dictionary<string, double> BuildingTypeRates = new Dictionary<string, double>
        {
            { "Residential (Low Cost)", 110 },    // BCISM 2022: RM 84-130/sqft (terrace), adj. 2024
            { "Residential (Medium)", 180 },      // BCISM 2022: RM 126-200/sqft (semi-D/bungalow), adj. 2024
            { "Residential (High End)", 320 },     // JUBM 2024: RM 305-633/sqft (luxury high-rise)
            { "Commercial (Office)", 200 },        // BCISM 2022: RM 136-370/sqft (2-4 storey), adj. 2024
            { "Commercial (Retail)", 250 },        // JUBM 2024: RM 200-350/sqft (retail/mixed-use)
            { "Industrial (Warehouse)", 100 },     // Arcadis 2022: RM 88-108/sqft (heavy duty), adj. 2024
            { "Industrial (Factory)", 130 },       // Arcadis 2022: RM 107-155/sqft (light duty flatted), adj. 2024
            { "Institutional (School)", 250 },     // BCISM 2022: RM 200-513/sqft (conventional-IBS), adj. 2024
            { "Institutional (Hospital)", 450 },   // JUBM 2024: RM 370-650+/sqft (M&E intensive)
            { "Custom", 0 }
        };

        /// <summary>
        /// Calculate a quick cost estimate based on total floor area and rate per sqft
        /// </summary>
        public static SqftEstimate CalculateSqftEstimate(List<CostItem> items, string buildingType, double customRate = 0)
        {
            items ??= new List<CostItem>();
            double floorAreaM2 = items
                .Where(i => i.Category == "Floors" && i.Unit == "m²")
                .Sum(i => i.Quantity);

            double rate = string.IsNullOrEmpty(buildingType) ? 0 :
                buildingType == "Custom" ? customRate :
                BuildingTypeRates.ContainsKey(buildingType) ? BuildingTypeRates[buildingType] : 0;

            return new SqftEstimate
            {
                TotalFloorAreaM2 = floorAreaM2,
                RatePerSqft = rate,
                BuildingType = buildingType
            };
        }

        /// <summary>
        /// Calculate full cost summary from items
        /// </summary>
        public static CostSummary Calculate(List<CostItem> items)
        {
            items ??= new List<CostItem>();
            var summary = new CostSummary
            {
                TotalItems = items.Count,
                PricedItems = items.Count(i => i.UnitPrice > 0),
                PriceableItems = items.Count(i => IsAutoPriceable(i.Category)),
                PriceablePricedItems = items.Count(i => IsAutoPriceable(i.Category) && i.UnitPrice > 0),
                GrandTotal = items.Sum(i => i.TotalPrice)
            };

            // Group by Level
            summary.ByLevel = items
                .GroupBy(i => i.Level ?? "Unassigned")
                .Select(g => new CostGroup
                {
                    Name = g.Key,
                    ItemCount = g.Count(),
                    TotalCost = g.Sum(i => i.TotalPrice),
                    Items = g.OrderBy(i => i.Category).ThenBy(i => i.Name).ToList()
                })
                .OrderByDescending(g => g.TotalCost)
                .ToList();

            // Group by Category
            summary.ByCategory = items
                .GroupBy(i => i.Category ?? "Other")
                .Select(g => new CostGroup
                {
                    Name = g.Key,
                    ItemCount = g.Count(),
                    TotalCost = g.Sum(i => i.TotalPrice),
                    Items = g.OrderBy(i => i.Name).ToList()
                })
                .OrderByDescending(g => g.TotalCost)
                .ToList();

            // Calculate percentages
            if (summary.GrandTotal > 0)
            {
                foreach (var group in summary.ByLevel)
                    group.Percentage = (group.TotalCost / summary.GrandTotal) * 100;
                foreach (var group in summary.ByCategory)
                    group.Percentage = (group.TotalCost / summary.GrandTotal) * 100;
            }

            summary.LevelCount = summary.ByLevel.Count;

            return summary;
        }

        /// <summary>
        /// Calculate summary for a specific level only
        /// </summary>
        public static CostSummary CalculateForLevel(List<CostItem> items, string levelName)
        {
            var filtered = items.Where(i =>
                string.Equals(i.Level, levelName, System.StringComparison.OrdinalIgnoreCase)).ToList();
            return Calculate(filtered);
        }

        // Non-construction categories to hide from the component card
        private static readonly HashSet<string> ComponentSkipCategories = new HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase)
        {
            "Entourage", "Planting", "Parking", "Mass", "Site",
            "Topography", "Curtain Systems", "Curtain Panels",
            "Curtain Wall Mullions", "Curtain Wall Grids",
            "Wall Sweeps", "Fascias", "Reveals",
            "Top Rails", "Railing Rail Path Extension Lines",
            "Runs", "Stacked Walls", "Shaft Openings", "Ramps",
        };

        /// <summary>
        /// Categories that should NOT be auto-priced (skipped by AI pipeline and fallback estimation).
        /// These are typically sub-elements that are rolled into parent element prices in Malaysian QS practice
        /// (rebar in concrete, fittings in pipe runs, connections in steel framing, etc.)
        /// They remain visible in the item list but contribute 0 to the total unless manually priced.
        /// </summary>
        public static readonly HashSet<string> NoAutoPriceCategories = new HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase)
        {
            // Site / cosmetic
            "Entourage", "Planting", "Parking", "Mass", "Site", "Topography",

            // Structural sub-elements (rolled into concrete/steel pricing)
            "Structural Foundations", "Structural Connections",
            "Rebar Shape", "Structural Rebar", "Structural Stiffener",
            "Structural Trusses", "Structural Beam Systems",

            // MEP sub-elements (rolled into pipe/duct runs)
            "Pipe Fittings", "Pipe Accessories",
            "Duct Fittings", "Duct Accessories",
            "Flex Pipes", "Flex Ducts",

            // Curtain wall sub-elements (rolled into curtain wall panels)
            "Curtain Wall Mullions", "Curtain Panels", "Curtain Systems",
            "Curtain Wall Grids",

            // Wall/Roof/Stair sub-elements (rolled into parent pricing)
            "Wall Sweeps", "Fascias", "Reveals",
            "Top Rails", "Railing Rail Path Extension Lines",
            "Runs", "Stacked Walls",

            // Other non-priceable physical elements
            "Shaft Openings", "Ramps",
        };

        /// <summary>
        /// Returns true if the category should be auto-priced via AI matching and fallback estimation.
        /// </summary>
        public static bool IsAutoPriceable(string category) =>
            !NoAutoPriceCategories.Contains(category ?? "");

        /// <summary>
        /// Calculate cost breakdown by Revit component type.
        /// Groups by Category, then sub-groups by FamilyName + TypeName.
        /// Filters out non-construction categories from the display.
        /// </summary>
        public static ComponentSummary CalculateComponentSummary(List<CostItem> items)
        {
            items ??= new List<CostItem>();

            // Filter to construction-relevant categories only
            var filtered = items.Where(i =>
                !ComponentSkipCategories.Contains(i.Category ?? "")).ToList();

            var summary = new ComponentSummary
            {
                TotalItems = filtered.Count,
                TotalCost = filtered.Sum(i => i.TotalPrice)
            };

            summary.Groups = filtered
                .GroupBy(i => i.Category ?? "Other")
                .Select(catGroup =>
                {
                    var subGroups = catGroup
                        .GroupBy(i => $"{i.FamilyName ?? "Unknown"}: {i.TypeName ?? "Default"}")
                        .Select(sg => new ComponentSubGroup
                        {
                            Name = sg.Key,
                            ItemCount = sg.Count(),
                            UnpricedCount = sg.Count(i => i.UnitPrice <= 0),
                            TotalQuantity = sg.Sum(i => i.Quantity),
                            Unit = sg.First().Unit ?? "unit",
                            TotalCost = sg.Sum(i => i.TotalPrice),
                            AverageUnitPrice = sg.Any(i => i.UnitPrice > 0)
                                ? sg.Where(i => i.UnitPrice > 0).Average(i => i.UnitPrice)
                                : 0
                        })
                        .OrderByDescending(sg => sg.TotalCost)
                        .ToList();

                    return new ComponentGroup
                    {
                        Category = catGroup.Key,
                        ItemCount = catGroup.Count(),
                        UnpricedCount = catGroup.Count(i => i.UnitPrice <= 0),
                        TotalCost = catGroup.Sum(i => i.TotalPrice),
                        SubGroups = subGroups
                    };
                })
                .Where(g => g.TotalCost > 0 || g.ItemCount > 0)
                .OrderByDescending(g => g.TotalCost)
                .ToList();

            if (summary.TotalCost > 0)
            {
                foreach (var group in summary.Groups)
                    group.Percentage = (group.TotalCost / summary.TotalCost) * 100;
            }

            summary.TotalComponents = summary.Groups.Sum(g => g.SubGroups.Count);
            return summary;
        }
    }
}
