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
        /// Preset building type rates in RM per sqft for Malaysian construction
        /// </summary>
        public static readonly Dictionary<string, double> BuildingTypeRates = new Dictionary<string, double>
        {
            { "Residential (Low Cost)", 45 },
            { "Residential (Medium)", 75 },
            { "Residential (High End)", 120 },
            { "Commercial (Office)", 90 },
            { "Commercial (Retail)", 100 },
            { "Industrial (Warehouse)", 40 },
            { "Industrial (Factory)", 55 },
            { "Institutional (School)", 70 },
            { "Institutional (Hospital)", 150 },
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

        /// <summary>
        /// Calculate cost breakdown by Revit component type.
        /// Groups by Category, then sub-groups by FamilyName + TypeName.
        /// </summary>
        public static ComponentSummary CalculateComponentSummary(List<CostItem> items)
        {
            items ??= new List<CostItem>();
            var summary = new ComponentSummary
            {
                TotalItems = items.Count,
                TotalCost = items.Sum(i => i.TotalPrice)
            };

            summary.Groups = items
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
